namespace RentkuttCRM.Services;

/// <summary>
/// SMS-løp: sender en påminnelses-SMS til kunder som fortsatt står i status
/// «Påbegynt søknad» ~24 timer etter at søknaden ble registrert. Avsender er «Rentekutt»
/// (LinkMobility sin standard-avsender). Styres av innstillinger og sender kun én gang per sak.
/// </summary>
public class Paamindelse24tWorker : BackgroundService
{
    // Innstillingsnøkler (settes i Kommunikasjon-fanen).
    public const string KeyEnabled = "sms_24t_enabled";
    public const string KeyMal = "sms_24t_mal";
    public const string KeyMinTimer = "sms_24t_min_timer";       // send tidligst så mange timer etter registrering
    public const string KeyMaksTimer = "sms_24t_maks_timer";     // ikke send til saker eldre enn dette (hindrer masseutsending)
    public const string KeyIntervallMin = "sms_24t_intervall_min"; // hvor ofte løpet skanner

    public const int StandardMinTimer = 24;
    public const int StandardMaksTimer = 72;
    public const int StandardIntervallMin = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Paamindelse24tWorker> _log;

    public Paamindelse24tWorker(IServiceScopeFactory scopeFactory, ILogger<Paamindelse24tWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // La oppstart (migrasjoner) fullføre først.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            var intervallMin = StandardIntervallMin;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                intervallMin = Math.Max(5, await settings.GetIntAsync(KeyIntervallMin, StandardIntervallMin));

                var link = scope.ServiceProvider.GetRequiredService<LinkMobilityService>();
                if (link.ErKonfigurert)
                    await KjorSyklusAsync(scope, settings, ct);
                // Ikke konfigurert = stille hopp (staging/dev sender aldri SMS).
            }
            catch (Exception ex) { _log.LogError(ex, "24t-SMS-syklus feilet"); }

            try { await Task.Delay(TimeSpan.FromMinutes(intervallMin), ct); }
            catch { break; }
        }
    }

    // Sendevindu: alle SMS-løp sender kun innenfor dette (norsk tid). Vises også i widgeten.
    public const int VinduFraTime = 8;
    public const int VinduTilTime = 21;
    public static bool InnenforSendevindu(DateTime naaUtc)
    {
        var o = naaUtc.TilOslo();
        return o.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && o.Hour >= VinduFraTime && o.Hour < VinduTilTime;
    }

    private async Task KjorSyklusAsync(IServiceScope scope, SettingsService settings, CancellationToken ct)
    {
        var loepListe = (await scope.ServiceProvider.GetRequiredService<SmsLoepService>().HentAsync())
            .Where(l => l.Aktiv && !string.IsNullOrWhiteSpace(l.MalNavn)).ToList();
        if (loepListe.Count == 0) return;

        // Send kun innenfor sendevinduet (08–21 på hverdager). Utenfor → vent til neste syklus.
        if (!InnenforSendevindu(DateTime.UtcNow)) return;

        var sms = scope.ServiceProvider.GetRequiredService<SmsMalService>();
        var kundekort = scope.ServiceProvider.GetRequiredService<KundekortService>();
        var utsending = scope.ServiceProvider.GetRequiredService<SmsUtsendingService>();
        var bankSending = scope.ServiceProvider.GetRequiredService<BankSendingService>();
        var klaviyo = scope.ServiceProvider.GetRequiredService<KlaviyoService>();

        var maler = await sms.ListAsync();
        var alleKort = await kundekort.ListLettAsync();
        // Signeringslenke per KUNDE: hentes fra kundens egen Instabank-sending (nøklet på kundekort-id),
        // ikke «siste sending uansett bank» — slik at lenken garantert tilhører riktig kunde.
        var signPerKort = (await bankSending.SisteePerKortOgBankAsync())
            .Where(s => InstabankService.ErInstabankNavn(s.Bank) && s.KundekortId is not null && !string.IsNullOrWhiteSpace(s.SigningUrl))
            .GroupBy(s => s.KundekortId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.SendtAt).First().SigningUrl!);
        var naa = DateTime.UtcNow;
        int sendt = 0, feilet = 0;

        foreach (var loep in loepListe)
        {
            if (ct.IsCancellationRequested) break;
            var mal = maler.FirstOrDefault(m => m.Navn == loep.MalNavn);
            if (mal is null) { _log.LogWarning("SMS-løp «{Navn}»: fant ikke mal «{Mal}».", loep.Navn, loep.MalNavn); continue; }

            var sendEtter = Math.Max(1, loep.SendEtterTimer);
            var ikkeEldre = Math.Max(sendEtter + 1, loep.IkkeEldreTimer);
            var oevre = naa.AddHours(-sendEtter);    // minst så gammel
            var nedre = naa.AddHours(-ikkeEldre);    // men ikke eldre enn dette
            var type = "sms_loep:" + loep.Id;        // dedup: én gang per sak per løp
            var malKreverLenke = mal.Tekst.Contains("{signeringslenke}", StringComparison.OrdinalIgnoreCase);

            var kandidater = alleKort.Where(k =>
                k.Status == loep.TriggerStatus &&
                !string.IsNullOrWhiteSpace(k.Mobilnummer) &&
                k.CreatedAt <= oevre && k.CreatedAt >= nedre).ToList();

            foreach (var k in kandidater)
            {
                if (ct.IsCancellationRequested) break;
                if (await utsending.HarSendtOkAsync(k.Id, type)) continue;

                // Signeringslenke for NØYAKTIG denne kunden (kundekort-id-nøklet). Null hvis ingen finnes.
                var signeringslenke = signPerKort.TryGetValue(k.Id, out var u) ? u : null;
                // Sikkerhet: krever malen lenke, men kunden mangler en? Ikke send (unngå tom/feil lenke) —
                // prøves igjen neste syklus når lenken er fanget fra Instabank.
                if (malKreverLenke && string.IsNullOrWhiteSpace(signeringslenke))
                {
                    _log.LogInformation("SMS-løp «{Navn}»: {Id} mangler signeringslenke ennå — hopper over.", loep.Navn, k.Id);
                    continue;
                }

                var melding = SmsMalService.Flett(mal.Tekst, k, signeringslenke);
                var (ok, detalj) = await sms.SendRaaAsync(k.Mobilnummer, melding);
                await utsending.LoggAsync(k.Id, type, k.Mobilnummer, ok, detalj);
                if (ok) sendt++;
                else { feilet++; _log.LogWarning("SMS-løp «{Navn}» feilet for {Id}: {Detalj}", loep.Navn, k.Id, detalj); }

                // Valgfri Klaviyo-event med samme flettefelt som event-egenskaper.
                if (ok && !string.IsNullOrWhiteSpace(loep.KlaviyoEvent))
                    klaviyo.Fyr(k, loep.KlaviyoEvent!, SmsMalService.FletteEgenskaper(k, signeringslenke));

                try { await Task.Delay(300, ct); } catch { break; }
            }
        }

        if (sendt > 0) _log.LogInformation("SMS-løp: sendte {Sendt} meldinger.", sendt);
        if (feilet > 0)
            await AlarmAsync("sms-loep-feilet", "SMS-løp feilet",
                $"{feilet} av {sendt + feilet} SMS-er feilet i siste syklus. Se LinkMobility-status.");
    }

    // Alarmering i eget scope — skal aldri kunne velte workeren.
    private async Task AlarmAsync(string noekkel, string tittel, string detalj)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alarm = scope.ServiceProvider.GetRequiredService<AlarmService>();
            await alarm.RaiseAsync("sms", tittel, detalj, kilde: "SMS-løp", noekkel: noekkel);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke reise SMS-alarm"); }
    }
}
