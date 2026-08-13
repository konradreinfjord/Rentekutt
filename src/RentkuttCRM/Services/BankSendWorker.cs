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
    private readonly DatabaseMigrator _migrator;
    private readonly IHostEnvironment _env;

    public BankSendWorker(IServiceScopeFactory scopeFactory, ILogger<BankSendWorker> log, DatabaseMigrator migrator, IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _migrator = migrator;
        _env = env;
    }

    // Reis en alarm i egen scope (alarmering skal aldri kunne velte workeren).
    private async Task AlarmAsync(string type, string tittel, string? detalj, string alvorlighet, string? noekkel)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alarm = scope.ServiceProvider.GetRequiredService<AlarmService>();
            await alarm.RaiseAsync(type, tittel, detalj, alvorlighet, "Sendekø", noekkel);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Alarm ({Type}) feilet", type); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // La oppstart (migrasjoner m.m.) fullføre først.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }

        // Infrastruktur-alarmer (migrasjon/DB/kryptering) reises KUN fra produksjon. Dev/lokale
        // instanser mangler bevisst Gdpr__FieldKey og ConnectionStrings:Postgres, og skal derfor
        // ikke forurense den delte alarmtabellen hver gang de restartes.
        if (_env.IsProduction())
        {
            using var scope = _scopeFactory.CreateScope();
            var alarm = scope.ServiceProvider.GetRequiredService<AlarmService>();

            // Alarm hvis migrasjonene ikke når prod (vanligste driftsstopp for nye funksjoner).
            var st = _migrator.Status;
            if (!st.Konfigurert)
                await alarm.RaiseAsync("Migrasjon", "Databasemigrering er ikke konfigurert",
                    "ConnectionStrings:Postgres mangler i Azure — nye tabeller/kolonner når ikke prod.",
                    AlarmService.Alvorlighet.Kritisk, "Sendekø", "migrasjon-ikke-konfigurert");
            else if (!st.Kjort && !string.IsNullOrWhiteSpace(st.SisteFeil))
                // Teknisk feil på DB-tilkoblingen (f.eks. 28P01 feil passord etter nøkkelrotasjon):
                // migrasjoner OG GDPR-jobber stopper stille. Gjør det synlig som kritisk alarm.
                await alarm.RaiseAsync("Migrasjon", "Databasetilkobling feilet — migrasjoner kjører ikke",
                    $"ConnectionStrings:Postgres virker ikke: {st.SisteFeil}. Nye kolonner (f.eks. boliglån) og GDPR-jobber stopper til passordet i tilkoblingsstrengen oppdateres.",
                    AlarmService.Alvorlighet.Kritisk, "Sendekø", "migrasjon-tilkobling-feil");
            else if (st.Feilet.Count > 0 || st.Utestaende.Count > 0)
                await alarm.RaiseAsync("Migrasjon", "Databasemigrasjoner utestående/feilet",
                    $"Utestående: {(st.Utestaende.Count > 0 ? string.Join(", ", st.Utestaende) : "—")}. Feilet: {(st.Feilet.Count > 0 ? string.Join("; ", st.Feilet) : "—")}",
                    AlarmService.Alvorlighet.Kritisk, "Sendekø", "migrasjon-feil");
            else
            {
                // DB er frisk → lukk ev. gamle migrasjon/DB-alarmer fra et tidligere uheldig
                // instans-/deploy-vindu, så de ikke blir stående og villede.
                await alarm.LosOppNoekkelAsync("migrasjon-ikke-konfigurert", "System (DB frisk)");
                await alarm.LosOppNoekkelAsync("migrasjon-tilkobling-feil", "System (DB frisk)");
                await alarm.LosOppNoekkelAsync("migrasjon-feil", "System (DB frisk)");
            }

            // «Kryptering AV» synlig som kritisk alarm — uten nøkkel lagres nye fnr i klartekst.
            try
            {
                if (!scope.ServiceProvider.GetRequiredService<CryptoService>().IsEnabled)
                {
                    await alarm.RaiseAsync("Kryptering", "Feltnivåkryptering er AV",
                        "Gdpr__FieldKey mangler — nye fødselsnummer lagres i KLARTEKST. Sett nøkkelen og kjør bakfylling.",
                        AlarmService.Alvorlighet.Kritisk, "Sendekø", "kryptering-av");
                }
                else
                {
                    // Nøkkelen ER lastet → lukk ev. gamle «kryptering AV»-alarmer.
                    await alarm.LosOppNoekkelAsync("kryptering-av", "System (nøkkel lastet)");
                    await alarm.LosOppNoekkelAsync("kryptering-av-klartekst", "System (nøkkel lastet)");
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke sjekke krypteringsstatus"); }
        }

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

                // Instanser uten krypteringsnøkkel skal IKKE behandle køen — de ville ikke klart
                // å dekryptere fødselsnummeret, og ville feilmarkert sendinger. La en instans MED
                // nøkkelen (scale-out) plukke dem i stedet. Sendingen blir stående i kø.
                if (!scope.ServiceProvider.GetRequiredService<CryptoService>().IsEnabled)
                {
                    try { await Task.Delay(Idle, ct); } catch { break; }
                    continue;
                }

                var ko = scope.ServiceProvider.GetRequiredService<BankSendingService>();
                var neste = (await ko.HentKoAsync(1)).FirstOrDefault();
                if (neste is not null)
                {
                    var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                    var instabank = scope.ServiceProvider.GetRequiredService<InstabankService>();
                    var samtykke = scope.ServiceProvider.GetRequiredService<SamtykkeService>();
                    var krypto = scope.ServiceProvider.GetRequiredService<CryptoService>();
                    utfall = await BehandleAsync(neste, ko, kunder, instabank, samtykke, krypto);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Sendekø-syklus feilet"); }

            await OppdaterBryterAsync(utfall);

            var gjordeKall = utfall != Utfall.IngenApiKall;
            try { await Task.Delay(gjordeKall ? Throttle : Idle, ct); }
            catch { break; }
        }
    }

    // Sikkerhetsbryter: tell sammenhengende forbigående feil; åpne pausen ved terskel.
    private async Task OppdaterBryterAsync(Utfall utfall)
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
                    await AlarmAsync("Sikkerhetsbryter", "Sending til bank er pauset",
                        $"{BryterTerskel}+ sammenhengende forbigående feil mot bank-API-et. Sending pauset i {BryterPause.TotalMinutes:N0} min for ikke å overbelaste banken.",
                        AlarmService.Alvorlighet.Kritisk, "sikkerhetsbryter");
                }
                break;
            // Varig/IngenApiKall påvirker ikke bryteren (vår-side/config-feil, ikke at banken er nede).
        }
    }

    private async Task<Utfall> BehandleAsync(BankSending s, BankSendingService ko, KundekortService kunder, InstabankService instabank, SamtykkeService samtykke, CryptoService krypto)
    {
        // Banker uten hardkodet API-sending registreres som manuelt videresendt (ingen API-kall).
        if (!InstabankService.ErInstabankNavn(s.Bank))
        {
            s.Status = SendStatus.Manuelt;
            s.Detalj = "Videresendt manuelt til banken.";
            await ko.OppdaterAsync(s);
            // Sendt til bank (manuelt) → utled kundekortstatus fra bankene («Sendt - I prosess»).
            if (s.KundekortId is { } mid)
            {
                var alle = await ko.ForKundeAsync(mid);
                var kort = await kunder.GetAsync(mid);
                await kunder.OppdaterStatusFraBankerAsync(mid, kort?.Status, alle, "System (sendt til bank)");
            }
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

        // Definitiv diagnose: hvis fnr fremdeles er kryptert HER, klarte ikke sendekø-prosessen
        // å dekryptere. Rapporter prosessens egen krypteringsstatus (IsEnabled + selvtest).
        if ((k.Foedselsnummer?.StartsWith("enc:1:", StringComparison.Ordinal) ?? false)
            || (k.KundeId?.StartsWith("enc:1:", StringComparison.Ordinal) ?? false))
        {
            string selvtest;
            try { var t = krypto.Beskytt("12345678901"); selvtest = (krypto.ErBeskyttet(t) && krypto.Avdekk(t) == "12345678901") ? "OK" : "FEILET"; }
            catch { selvtest = "FEILET"; }
            s.Status = SendStatus.Feilet;
            s.Detalj = $"Kryptering i sendekø-prosessen: IsEnabled={krypto.IsEnabled}, selvtest={selvtest} — men fnr forble kryptert (enc:1:). Prosessen/instansen som kjører sendekøen har ikke riktig Gdpr__FieldKey.";
            await kunder.SetStatusAsync(id, KundekortService.StatusTekniskFeil);
            await ko.OppdaterAsync(s);
            return Utfall.Varig;
        }

        // GDPR-sperre: krev gyldig samtykke (Gjeldsregister og kredittsjekk) før oversendelse.
        // Eldre lead uten samtykke-rad dekkes av det boolske feltet på kundekortet.
        var harSamtykke = await samtykke.HarGyldigEllerLegacyAsync(id, SamtykkeService.FormaalKreditt, k.SamtykkeGjeldsregisterKredittsjekk);
        if (!harSamtykke)
        {
            s.Status = SendStatus.Feilet;
            s.Detalj = "Mangler gyldig samtykke (Gjeldsregister og kredittsjekk) — ikke sendt til bank.";
            await ko.OppdaterAsync(s);
            await kunder.SetStatusAsync(id, KundekortService.StatusTekniskFeil);
            return Utfall.Varig;
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
            // API-sending til bank feilet endelig → sett kundekort-status «Feilet i sending».
            await kunder.SetStatusAsync(id, KundekortService.StatusTekniskFeil);
            await AlarmAsync("Banksending", $"Sending til {s.Bank} feilet",
                $"{s.KundeNavn ?? "Kunde"}{(string.IsNullOrWhiteSpace(s.Produkt) ? "" : $" · {s.Produkt}")}: {r.Detalj}",
                AlarmService.Alvorlighet.Advarsel, $"banksending-feilet-{s.KundekortId}");
        }
        await ko.OppdaterAsync(s);
        // Vellykket sending til bank → utled kundekortstatus fra bankene («Sendt - I prosess»
        // til Instabank-synk gir endelig utfall).
        if (utfall == Utfall.Ok)
        {
            var alle = await ko.ForKundeAsync(id);
            await kunder.OppdaterStatusFraBankerAsync(id, k.Status, alle, "System (sendt til bank)");
        }
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
