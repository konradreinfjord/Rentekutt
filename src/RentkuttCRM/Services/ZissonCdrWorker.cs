namespace RentkuttCRM.Services;

/// <summary>
/// Fyller utfall/varighet på utgående dialer-anrop. Spør Zisson CDR (ConversationSessions) på
/// uavklarte anrop, og når samtalen er avsluttet skrives utfall (svart/ikke svart + taletid) til
/// både <c>dialer_anrop</c> og kundekortets logg. Slutter å spørre når anropet er avklart.
/// Kjører kun i produksjon — dev/lokale instanser deler prod-databasen og skal ikke endre saker.
/// </summary>
public class ZissonCdrWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ZissonCdrWorker> _log;

    public ZissonCdrWorker(IServiceScopeFactory scopeFactory, IHostEnvironment env, ILogger<ZissonCdrWorker> log)
    {
        _scopeFactory = scopeFactory;
        _env = env;
        _log = log;
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
                var zisson = scope.ServiceProvider.GetRequiredService<ZissonService>();
                if (await zisson.ErKonfigurertAsync())
                {
                    var dialer = scope.ServiceProvider.GetRequiredService<DialerService>();
                    var logg = scope.ServiceProvider.GetRequiredService<LoggService>();

                    var uavklarte = await dialer.UavklarteAsync(timer: 12);
                    int oppdatert = 0;
                    foreach (var a in uavklarte)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(a.Zid)) continue;

                        // Litt margin rundt starttidspunktet, til «nå».
                        var fra = a.StartetAt.AddMinutes(-2);
                        var til = DateTime.UtcNow.AddMinutes(2);
                        var r = await zisson.HentUtfallAsync(a.Zid!, fra, til);
                        if (!r.Funnet || !r.Avsluttet) continue;   // ikke i CDR ennå / pågår fortsatt

                        var utfall = r.Svart ? DialerService.UtfallSvart : DialerService.UtfallIkkeSvart;
                        await dialer.SettUtfallAsync(a.Id, DialerService.StatusFerdig, utfall, r.TaletidSek);

                        var tekst = r.Svart
                            ? $"📞 Utgående anrop besvart – varighet {FormatVarighet(r.TaletidSek)}."
                            : "📞 Utgående anrop ikke besvart.";
                        await logg.LoggAsync(a.KundekortId, "System (Dialer-synk)", tekst, "anrop");
                        oppdatert++;

                        try { await Task.Delay(300, ct); } catch { break; }
                    }
                    if (oppdatert > 0) _log.LogInformation("Dialer CDR-synk: {N} anrop oppdatert.", oppdatert);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Dialer CDR-synk-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }

    private static string FormatVarighet(int? sek)
    {
        var s = sek ?? 0;
        return s >= 60 ? $"{s / 60} min {s % 60} s" : $"{s} s";
    }
}
