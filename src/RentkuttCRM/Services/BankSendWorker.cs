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

    // Sikkerhetsbryter (circuit breaker): stopper sending når banken gjentatte ganger
    // svarer med forbigående feil (nett/timeout/429/5xx) — så vi ikke hamrer løs på et
    // API som allerede sliter. Åpnes etter N sammenhengende forbigående feil, og holder
    // en pause før nye forsøk. Lukkes ved første vellykkede sending.
    private const int BryterTerskel = 5;
    private static readonly TimeSpan BryterPause = TimeSpan.FromMinutes(5);
    private int _sammenhengendeFeil;
    private DateTime _pauseTil = DateTime.MinValue;

    private enum Utfall { IngenApiKall, Ok, Forbigaaende, Varig }

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
            // Sikkerhetsbryter åpen ⇒ ikke rør API-et før pausen er over.
            if (DateTime.UtcNow < _pauseTil)
            {
                try { await Task.Delay(Idle, ct); } catch { break; }
                continue;
            }

            var utfall = Utfall.IngenApiKall;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ko = scope.ServiceProvider.GetRequiredService<BankSendingService>();
                var neste = (await ko.HentKoAsync(1)).FirstOrDefault();
                if (neste is not null)
                {
                    var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                    var instabank = scope.ServiceProvider.GetRequiredService<InstabankService>();
                    utfall = await BehandleAsync(neste, ko, kunder, instabank);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Sendekø-syklus feilet"); }

            OppdaterBryter(utfall);

            var gjordeKall = utfall != Utfall.IngenApiKall;
            try { await Task.Delay(gjordeKall ? Throttle : Idle, ct); }
            catch { break; }
        }
    }

    // Sikkerhetsbryter: tell sammenhengende forbigående feil; åpne pausen ved terskel.
    private void OppdaterBryter(Utfall utfall)
    {
        switch (utfall)
        {
            case Utfall.Ok:
                if (_sammenhengendeFeil > 0) _log.LogInformation("Bank-sending OK igjen — nullstiller sikkerhetsbryter");
                _sammenhengendeFeil = 0;
                break;
            case Utfall.Forbigaaende:
                _sammenhengendeFeil++;
                if (_sammenhengendeFeil >= BryterTerskel)
                {
                    _pauseTil = DateTime.UtcNow + BryterPause;
                    _sammenhengendeFeil = 0;
                    _log.LogWarning("Sikkerhetsbryter utløst: {N}+ sammenhengende forbigående feil mot bank — pauser sending i {Min} min",
                        BryterTerskel, BryterPause.TotalMinutes);
                }
                break;
            // Varig/IngenApiKall påvirker ikke bryteren (vår-side/config-feil, ikke at banken er nede).
        }
    }

    private async Task<Utfall> BehandleAsync(BankSending s, BankSendingService ko, KundekortService kunder, InstabankService instabank)
    {
        // Banker uten hardkodet API-sending registreres som manuelt videresendt (ingen API-kall).
        if (!InstabankService.ErInstabankNavn(s.Bank))
        {
            s.Status = SendStatus.Manuelt;
            s.Detalj = "Videresendt manuelt til banken.";
            await ko.OppdaterAsync(s);
            return Utfall.IngenApiKall;
        }

        if (s.KundekortId is not { } id)
        {
            s.Status = SendStatus.Feilet; s.Detalj = "Mangler kundekort.";
            await ko.OppdaterAsync(s); return Utfall.IngenApiKall;
        }
        var k = await kunder.GetAsync(id);
        if (k is null)
        {
            s.Status = SendStatus.Feilet; s.Detalj = "Fant ikke kundekortet.";
            await ko.OppdaterAsync(s); return Utfall.IngenApiKall;
        }

        var r = await instabank.SendSoknadAsync(k, s.ProduktKode);
        s.Forsok += 1;
        Utfall utfall;
        if (r.Ok)
        {
            s.Status = SendStatus.Sendt;
            s.EksternRef = r.ExternalReference;
            s.SigningUrl = r.SigningUrl;
            s.Detalj = r.Detalj;
            utfall = Utfall.Ok;
        }
        else if (ErForbigaaende(r.Detalj) && s.Forsok < MaxForsok)
        {
            s.Status = SendStatus.IKo;   // prøv igjen senere
            s.Detalj = $"Forsøk {s.Forsok} utsatt: {r.Detalj}";
            utfall = Utfall.Forbigaaende;
        }
        else
        {
            s.Status = SendStatus.Feilet;
            s.Detalj = r.Detalj;
            // Nådde vi maks forsøk på en forbigående feil, teller det fortsatt som at banken sliter.
            utfall = ErForbigaaende(r.Detalj) ? Utfall.Forbigaaende : Utfall.Varig;
        }
        await ko.OppdaterAsync(s);
        return utfall;
    }

    // Forbigående feil vi kan prøve på nytt (rate limit / nettverk) — ikke varige valideringsfeil.
    private static bool ErForbigaaende(string detalj) =>
        detalj.Contains("NULL/empty", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("Nettverksfeil", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || detalj.Contains("429")
        || detalj.Contains("rate", StringComparison.OrdinalIgnoreCase);
}
