namespace RentkuttCRM.Services;

/// <summary>
/// Statussynk mot Instabank: spør (GET) hvert 5. minutt på søknader som er sendt til Instabank,
/// men ennå ikke er endelig avklart. Setter per-bank UTFALL på sendingen, og lar kundekortets
/// samlede status utledes fra alle bankene (fler-bank-logikk). Slutter å spørre når saken er
/// avklart (Utbetalt/Avslått/Kansellert). Kjører kun i produksjon — dev/lokale instanser deler
/// prod-databasen og skal ikke endre saker.
/// </summary>
public class InstabankStatusWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<InstabankStatusWorker> _log;

    public InstabankStatusWorker(IServiceScopeFactory scopeFactory, IHostEnvironment env, ILogger<InstabankStatusWorker> log)
    {
        _scopeFactory = scopeFactory;
        _env = env;
        _log = log;
    }

    // Instabank-status → per-bank utfall. New/Control = fortsatt under behandling (ingen endring).
    private static string? MapUtfall(string? instabankStatus) => instabankStatus switch
    {
        "Complete" => SendUtfall.Utbetalt,
        "Approved" or "DocumentsSent" or "DocumentsComplete" => SendUtfall.Innvilget,
        "Rejected" => SendUtfall.Avslatt,
        "Cancelled" => SendUtfall.Kansellert,
        _ => null,
    };

    // Instabank har ingen push tilbake til oss — vi må polle. Kun «Complete» (utbetalt) er endelig.
    // Iht. Instabanks doc går «Rejected» videre til «Cancelled» (manuelt eller automatisk etter noen
    // dager); noen annen vei ut av avslag er ikke dokumentert, men support har antydet at saker kan
    // snus. Derfor: negative utfall sjekkes på nytt ÉN gang i døgnet i 30 dager — billig, men fanger
    // både auto-kansellering og en eventuell snuing. Aktive saker polles hvert 5. minutt som før.
    private static readonly TimeSpan PollEtterNegativtUtfall = TimeSpan.FromDays(30);

    private static bool FerdigPollet(BankSending s)
    {
        if (s.Utfall == SendUtfall.Utbetalt) return true;
        if (s.Utfall is SendUtfall.Avslatt or SendUtfall.Kansellert)
        {
            if (DateTime.UtcNow - s.SendtAt > PollEtterNegativtUtfall) return true;
            // Kun i døgnets første 5-minutters-syklus etter kl. 06 UTC (én sjekk per døgn).
            var naa = DateTime.UtcNow;
            return !(naa.Hour == 6 && naa.Minute < 5);
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }
        if (!_env.IsProduction()) return;   // aldri endre saker fra dev/lokal (delt prod-DB)

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var instabank = scope.ServiceProvider.GetRequiredService<InstabankService>();
                if (await instabank.ErKonfigurertAsync())
                {
                    var sendinger = scope.ServiceProvider.GetRequiredService<BankSendingService>();
                    var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                    var kortMap = (await kunder.ListLettAsync()).ToDictionary(k => k.Id, k => k.Status);
                    var perKort = await sendinger.SisteePerKundekortAsync();

                    int oppdatert = 0;
                    foreach (var (kortId, siste) in perKort)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!InstabankService.ErInstabankNavn(siste.Bank) || string.IsNullOrWhiteSpace(siste.EksternRef)) continue;
                        if (!kortMap.TryGetValue(kortId, out var status)) continue;
                        if (status == KundekortService.StatusUtbetalt) continue;   // utbetalt = endelig
                        if (FerdigPollet(siste)) continue;                          // utbetalt, eller negativt utfall > 30 dager

                        var r = await instabank.HentStatusAsync(siste.EksternRef!);
                        if (r.Ok)
                        {
                            // Fang/oppdater signeringslenken (kan komme/endres ved innvilgelse) så den
                            // er fersk for SMS-/Klaviyo-fletting.
                            if (!string.IsNullOrWhiteSpace(r.SigningUrl) && r.SigningUrl != siste.SigningUrl)
                            {
                                await sendinger.SetSigningUrlAsync(siste.Id, r.SigningUrl);
                                siste.SigningUrl = r.SigningUrl;
                            }
                            // Fang innvilget beløp + Instabank-id fra responsen (vises på kundekortet).
                            if (r.InnvilgetBelop is not null || !string.IsNullOrWhiteSpace(r.InstabankId))
                                await sendinger.SetInstabankDetaljerAsync(siste.Id, r.InnvilgetBelop, r.InstabankId);

                            var utfall = MapUtfall(r.Status);
                            // Signeringslenke i retur = Instabank har gitt et signerbart tilbud ⇒ søknaden
                            // er innvilget. Kjente «Complete/Rejected» (fra MapUtfall) har forrang; ellers
                            // promoterer vi til «Innvilget» så ukjente tilbuds-/signeringsstatuser fanges
                            // (f.eks. «Offered»/«Signing») i stedet for å bli stående uendret.
                            if (utfall is null && !string.IsNullOrWhiteSpace(siste.SigningUrl))
                                utfall = SendUtfall.Innvilget;
                            if (utfall is not null && utfall != siste.Utfall)
                            {
                                await sendinger.SetUtfallAsync(siste.Id, utfall);
                                // Aggreger kundekortets status fra ALLE bankenes utfall.
                                var alle = await sendinger.ForKundeAsync(kortId);
                                await kunder.OppdaterStatusFraBankerAsync(kortId, status, alle, "System (Instabank-synk)");
                                // Ved avslag: lagre undergrunn fra Instabank-responsen (hvis oppgitt).
                                if (utfall == SendUtfall.Avslatt)
                                    await kunder.SetAvslagGrunnAsync(kortId,
                                        (string.IsNullOrWhiteSpace(r.Reason) ? "Avslått av Instabank" : r.Reason) + " (Instabank)");
                                // Transaksjonell hendelse: SMS + Klaviyo/e-post ved innvilgelse i Instabank-API-et.
                                if (utfall == SendUtfall.Innvilget
                                    && await kunder.GetAsync(kortId) is { } kort)
                                {
                                    var ev = scope.ServiceProvider.GetRequiredService<InstabankInnvilgetEventService>();
                                    await ev.FyrInnvilgetAsync(kort, siste.SigningUrl);
                                }
                                oppdatert++;
                            }
                        }
                        try { await Task.Delay(500, ct); } catch { break; }
                    }
                    if (oppdatert > 0) _log.LogInformation("Instabank-synk: {N} sak(er) oppdatert.", oppdatert);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Instabank-statussynk-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }
}
