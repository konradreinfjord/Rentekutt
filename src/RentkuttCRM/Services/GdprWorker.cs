namespace RentkuttCRM.Services;

/// <summary>Kjører GDPR-anonymisering/sletting daglig, styrt av innstillingene
/// gdpr_anonymize_months / gdpr_delete_months. Logger resultatet.</summary>
public class GdprWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GdprWorker> _log;

    public GdprWorker(IServiceScopeFactory scopeFactory, ILogger<GdprWorker> log)
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
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var gdpr = scope.ServiceProvider.GetRequiredService<GdprService>();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var anonMnd = await settings.GetIntAsync("gdpr_anonymize_months", 12);
                var slettMnd = await settings.GetIntAsync("gdpr_delete_months", 24);
                var (anon, slett, feil) = await gdpr.KjorAsync(anonMnd, slettMnd, torrkjor: false, ct);
                if (feil is not null) _log.LogWarning("GDPR-jobb hoppet over / feilet: {Feil}", feil);
            }
            catch (Exception ex) { _log.LogError(ex, "GDPR-jobb-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }
}
