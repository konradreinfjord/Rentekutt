namespace RentkuttCRM.Services;

/// <summary>
/// Sikker sendekø: plukker søknader fra køen (status «I kø») og sender dem til bank
/// ETT kall om gangen med fast pause imellom — slik at vi aldri bombarderer bank-API-et
/// (rate limiting). Forbigående feil (rate limit / nettverk) beholdes i kø for nytt forsøk;
/// varige feil markeres «Feilet». Køen ligger i databasen, så den overlever restart.
/// </summary>
public class BankSendWorker : BackgroundService
{
    // Minimum mellom to API-kall (throttle). Instabank tåler ikke rask bombardering.
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);
    private const int MaxForsok = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BankSendWorker> _log;

    public BankSendWorker(IServiceScopeFactory scopeFactory, ILogger<BankSendWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // La oppstart (migrasjoner m.m.) fullføre først.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            var apiKall = false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ko = scope.ServiceProvider.GetRequiredService<BankSendingService>();
                var neste = (await ko.HentKoAsync(1)).FirstOrDefault();
                if (neste is not null)
                {
                    var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                    var instabank = scope.ServiceProvider.GetRequiredService<InstabankService>();
                    apiKall = await BehandleAsync(neste, ko, kunder, instabank);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Sendekø-syklus feilet"); }

            try { await Task.Delay(apiKall ? Throttle : Idle, ct); }
            catch { break; }
        }
    }

    /// <returns>True hvis det ble gjort et API-kall (⇒ throttle før neste).</returns>
    private async Task<bool> BehandleAsync(BankSending s, BankSendingService ko, KundekortService kunder, InstabankService instabank)
    {
        // Banker uten hardkodet API-sending registreres som manuelt videresendt (ingen API-kall).
        if (!InstabankService.ErInstabankNavn(s.Bank))
        {
            s.Status = SendStatus.Manuelt;
            s.Detalj = "Videresendt manuelt til banken.";
            await ko.OppdaterAsync(s);
            return false;
        }

        if (s.KundekortId is not { } id)
        {
            s.Status = SendStatus.Feilet; s.Detalj = "Mangler kundekort.";
            await ko.OppdaterAsync(s); return false;
        }
        var k = await kunder.GetAsync(id);
        if (k is null)
        {
            s.Status = SendStatus.Feilet; s.Detalj = "Fant ikke kundekortet.";
            await ko.OppdaterAsync(s); return false;
        }

        var r = await instabank.SendSoknadAsync(k);
        s.Forsok += 1;
        if (r.Ok)
        {
            s.Status = SendStatus.Sendt;
            s.EksternRef = r.ExternalReference;
            s.SigningUrl = r.SigningUrl;
            s.Detalj = r.Detalj;
        }
        else if (ErForbigaaende(r.Detalj) && s.Forsok < MaxForsok)
        {
            s.Status = SendStatus.IKo;   // prøv igjen senere
            s.Detalj = $"Forsøk {s.Forsok} utsatt: {r.Detalj}";
        }
        else
        {
            s.Status = SendStatus.Feilet;
            s.Detalj = r.Detalj;
        }
        await ko.OppdaterAsync(s);
        return true;
    }

    // Forbigående feil vi kan prøve på nytt (rate limit / nettverk) — ikke varige valideringsfeil.
    private static bool ErForbigaaende(string detalj) =>
        detalj.Contains("NULL/empty", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("Nettverksfeil", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("429")
        || detalj.Contains("rate", StringComparison.OrdinalIgnoreCase);
}
