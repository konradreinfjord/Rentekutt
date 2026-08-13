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

    private static bool UtfallAvklart(string? utfall) =>
        utfall is SendUtfall.Utbetalt or SendUtfall.Avslatt or SendUtfall.Kansellert;

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
                        if (KundekortService.StatuserAvklart.Contains(status)) continue;   // saken er ferdig
                        if (UtfallAvklart(siste.Utfall)) continue;                          // denne sendingen er ferdig

                        var r = await instabank.HentStatusAsync(siste.EksternRef!);
                        if (r.Ok)
                        {
                            var utfall = MapUtfall(r.Status);
                            if (utfall is not null && utfall != siste.Utfall)
                            {
                                await sendinger.SetUtfallAsync(siste.Id, utfall);
                                // Aggreger kundekortets status fra ALLE bankenes utfall.
                                var alle = await sendinger.ForKundeAsync(kortId);
                                await kunder.OppdaterStatusFraBankerAsync(kortId, status, alle, "System (Instabank-synk)");
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
