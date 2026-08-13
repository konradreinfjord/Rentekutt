namespace RentkuttCRM.Services;

/// <summary>
/// Statussynk mot Instabank: spør (GET) hvert 5. minutt på søknader som er sendt til Instabank,
/// men ennå ikke har endelig utfall. Oppdaterer kundekortets status fra svaret. Når søknaden er
/// avklart (godkjent/tilbud utsendt eller avslått) slutter vi å spørre. Kjører kun i produksjon —
/// dev/lokale instanser deler prod-databasen og skal ikke endre saker.
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

    // Endelig utfall → slutt å spørre.
    private static bool ErAvklart(string? status) =>
        status is KundekortService.StatusAvslatt
               or KundekortService.StatusFullfort
               or KundekortService.StatusTilbudUtsendt;

    // Instabank-status → kundekort-status. New/Control = fortsatt under behandling (ingen endring).
    private static string? MapStatus(string? instabankStatus) => instabankStatus switch
    {
        "Complete" => KundekortService.StatusFullfort,
        "Rejected" or "Cancelled" => KundekortService.StatusAvslatt,
        "Approved" or "DocumentsSent" or "DocumentsComplete" => KundekortService.StatusTilbudUtsendt,
        _ => null,
    };

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
                    var perKort = await sendinger.SisteePerKundekortAsync();
                    var kortMap = (await kunder.ListLettAsync()).ToDictionary(k => k.Id);

                    int oppdatert = 0, sjekket = 0;
                    foreach (var (kortId, s) in perKort)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!InstabankService.ErInstabankNavn(s.Bank) || string.IsNullOrWhiteSpace(s.EksternRef)) continue;
                        if (!kortMap.TryGetValue(kortId, out var k)) continue;
                        if (ErAvklart(k.Status)) continue;   // allerede endelig utfall → hopp over

                        sjekket++;
                        var r = await instabank.HentStatusAsync(s.EksternRef!);
                        if (r.Ok)
                        {
                            var ny = MapStatus(r.Status);
                            if (ny is not null && ny != k.Status)
                            {
                                await kunder.SetStatusAsync(k.Id, ny, "System (Instabank-synk)");
                                oppdatert++;
                            }
                        }
                        try { await Task.Delay(500, ct); } catch { break; }   // ikke bombardér API-et
                    }
                    if (oppdatert > 0)
                        _log.LogInformation("Instabank-synk: {Opp} av {Sjekk} søknader oppdatert.", oppdatert, sjekket);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Instabank-statussynk-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }
}
