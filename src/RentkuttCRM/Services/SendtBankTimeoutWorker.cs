namespace RentkuttCRM.Services;

/// <summary>
/// Setter saker som har stått i «Sendt bank» lenger enn innstilt antall dager
/// (sendt_bank_timeout_dager) automatisk til «Sendt til bank - Timeout». 0 dager = av.
/// Kjører kun i produksjon — dev/lokale instanser deler prod-databasen og skal ikke endre saker.
/// </summary>
public class SendtBankTimeoutWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SendtBankTimeoutWorker> _log;

    public SendtBankTimeoutWorker(IServiceScopeFactory scopeFactory, IHostEnvironment env, ILogger<SendtBankTimeoutWorker> log)
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
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var dager = await settings.GetIntAsync(KundekortService.KeySendtBankTimeoutDager, 0);
                if (dager > 0)
                {
                    var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                    var grense = DateTime.UtcNow.AddDays(-dager);
                    var forfalt = (await kunder.ListLettAsync()).Where(k =>
                        k.Status == KundekortService.StatusSendtBank &&
                        k.SendtBankAt is { } sendt && sendt < grense).ToList();

                    foreach (var k in forfalt)
                    {
                        if (ct.IsCancellationRequested) break;
                        await kunder.SetStatusAsync(k.Id, KundekortService.StatusSendtBankTimeout, "System (timeout)");
                    }
                    if (forfalt.Count > 0)
                        _log.LogInformation("Sendt-bank-timeout: {N} sak(er) satt til timeout (>{Dager} dager).", forfalt.Count, dager);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Sendt-bank-timeout-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }
}
