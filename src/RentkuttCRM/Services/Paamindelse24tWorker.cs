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

                if (await settings.GetBoolAsync(KeyEnabled, false))
                {
                    var link = scope.ServiceProvider.GetRequiredService<LinkMobilityService>();
                    if (link.ErKonfigurert)
                        await KjorSyklusAsync(scope, settings, ct);
                    else
                        _log.LogWarning("24t-SMS: LinkMobility ikke konfigurert — hopper over.");
                }
            }
            catch (Exception ex) { _log.LogError(ex, "24t-SMS-syklus feilet"); }

            try { await Task.Delay(TimeSpan.FromMinutes(intervallMin), ct); }
            catch { break; }
        }
    }

    private async Task KjorSyklusAsync(IServiceScope scope, SettingsService settings, CancellationToken ct)
    {
        var sms = scope.ServiceProvider.GetRequiredService<SmsMalService>();
        var kundekort = scope.ServiceProvider.GetRequiredService<KundekortService>();
        var utsending = scope.ServiceProvider.GetRequiredService<SmsUtsendingService>();

        var malNavn = await settings.GetAsync(KeyMal);
        if (string.IsNullOrWhiteSpace(malNavn)) { _log.LogInformation("24t-SMS: ingen mal valgt — hopper over."); return; }
        var mal = (await sms.ListAsync()).FirstOrDefault(m => m.Navn == malNavn);
        if (mal is null) { _log.LogWarning("24t-SMS: fant ikke mal «{Mal}».", malNavn); return; }

        var minTimer = Math.Max(1, await settings.GetIntAsync(KeyMinTimer, StandardMinTimer));
        var maksTimer = Math.Max(minTimer + 1, await settings.GetIntAsync(KeyMaksTimer, StandardMaksTimer));

        var naa = DateTime.UtcNow;
        var oevreGrense = naa.AddHours(-minTimer);   // eldre enn dette (minst så gammel)
        var nedreGrense = naa.AddHours(-maksTimer);  // men ikke eldre enn dette

        var kandidater = (await kundekort.ListLettAsync()).Where(k =>
            k.Status == KundekortService.StatusPaabegynt &&
            !string.IsNullOrWhiteSpace(k.Mobilnummer) &&
            k.CreatedAt <= oevreGrense &&
            k.CreatedAt >= nedreGrense).ToList();

        int sendt = 0, feilet = 0;
        foreach (var k in kandidater)
        {
            if (ct.IsCancellationRequested) break;
            if (await utsending.HarSendtOkAsync(k.Id, SmsUtsendingService.TypePaamindelse24t)) continue;

            var (ok, detalj) = await sms.SendTilKundeAsync(k.Mobilnummer, mal.Tekst, k.FulltNavn);
            await utsending.LoggAsync(k.Id, SmsUtsendingService.TypePaamindelse24t, k.Mobilnummer, ok, detalj);
            if (ok) sendt++;
            else { feilet++; _log.LogWarning("24t-SMS feilet for {Id}: {Detalj}", k.Id, detalj); }

            try { await Task.Delay(300, ct); } catch { break; } // ikke bombardér SMS-API-et
        }

        if (sendt > 0) _log.LogInformation("24t-SMS: sendte {Sendt} påminnelser.", sendt);
        if (feilet > 0)
            await AlarmAsync("sms-24t-feilet", "24-timers SMS feilet",
                $"{feilet} av {sendt + feilet} påminnelses-SMS-er feilet i siste syklus. Se LinkMobility-status.");
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
