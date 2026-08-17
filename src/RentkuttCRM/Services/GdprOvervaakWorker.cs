using System.Globalization;

namespace RentkuttCRM.Services;

/// <summary>
/// GDPR-overvåking (vedlegg A). Evalueres HVER TIME på tilstand — ikke på engangshendelser — og står
/// aktiv så lenge betingelsen er sann. Uavhengig av retensjonsjobbens egen direkte-Postgres-tilkobling:
///   • Alarm 1 — oppbevaringsjobben har ikke fullført på 48 t (eller aldri), med konsekvens i teksten.
///   • Alarm 2 — fødselsnummer i klartekst.
///   • Reparasjonsjobb — krypterer klartekst-fnr + regenererer HMAC (daglig, når nøkkelen er lastet),
///     så et kort feilkonfig-vindu ikke etterlater et permanent restlager i klartekst.
/// Kjører kun i produksjon (dev deler prod-DB og skal ikke forurense alarmene).
/// </summary>
public class GdprOvervaakWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromHours(1);
    private static readonly TimeSpan FullfortGrense = TimeSpan.FromHours(48);
    private static readonly TimeSpan ReparasjonIntervall = TimeSpan.FromHours(20);
    private const string KeyOverSiden = "gdpr_over_oppbevaring_siden";   // når «rader over oppbevaringstid» startet

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<GdprOvervaakWorker> _log;
    private DateTime? _sisteReparasjon;

    public GdprOvervaakWorker(IServiceScopeFactory scopeFactory, IHostEnvironment env, ILogger<GdprOvervaakWorker> log)
    {
        _scopeFactory = scopeFactory;
        _env = env;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { return; }
        if (!_env.IsProduction()) return;   // aldri endre saker / alarmer fra dev (delt prod-DB)

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var kjoring = scope.ServiceProvider.GetRequiredService<GdprKjoringService>();
                var kunder = scope.ServiceProvider.GetRequiredService<KundekortService>();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var alarm = scope.ServiceProvider.GetRequiredService<AlarmService>();
                var krypto = scope.ServiceProvider.GetRequiredService<CryptoService>();
                var naa = DateTime.UtcNow;

                // ---- Alarm 1: oppbevaringsjobben (sletting) har ikke fullført ----
                var slettMnd = await settings.GetIntAsync("gdpr_delete_months", 24);
                var siste = await kjoring.SisteFullfortAsync(GdprKjoringService.JobbSletting);
                var overOppbevaring = await kunder.TellOverOppbevaringstidAsync(slettMnd);
                var mTekst = overOppbevaring >= 0
                    ? $"{overOppbevaring} kundekort ligger over oppbevaringstiden."
                    : "Antall over oppbevaringstid kunne ikke telles.";

                // Eksplisitt null-håndtering: aldri fullført = alarm (ikke taus).
                if (siste is null || naa - siste.Value > FullfortGrense)
                {
                    var siden = siste is null ? "aldri" : siste.Value.TilOslo().ToString("dd.MM.yyyy HH:mm");
                    var doegn = siste is null ? "—" : ((int)(naa - siste.Value).TotalDays).ToString();
                    await alarm.RaiseAsync("GDPR", "Oppbevaringsjobben har ikke fullført",
                        $"Sletterutinen har ikke fullført siden {siden} ({doegn} døgn). {mTekst}",
                        AlarmService.Alvorlighet.Kritisk, "GDPR-jobb", "gdpr-sletting-forsinket");
                }
                else
                {
                    await alarm.LosOppNoekkelAsync("gdpr-sletting-forsinket", "System (jobb fullført)");
                    // Info-hjerteslag: siste vellykkede kjøring synlig i alarmlista (teller ikke som problem).
                    await alarm.RaiseAsync("GDPR", "Siste vellykkede oppbevaringsjobb",
                        $"Fullførte {siste.Value.TilOslo():dd.MM.yyyy HH:mm}. {mTekst}",
                        AlarmService.Alvorlighet.Info, "GDPR-jobb", "gdpr-sletting-ok");
                }

                // ---- Reparasjonsjobb: krypter klartekst + regenerer HMAC (daglig, når nøkkel er lastet) ----
                if (krypto.IsEnabled && (_sisteReparasjon is null || naa - _sisteReparasjon.Value > ReparasjonIntervall))
                {
                    var repId = await kjoring.StartAsync(GdprKjoringService.JobbReparasjon);
                    var (oppdatert, _, rfeil) = await kunder.KrypterEksisterendeAsync();
                    if (repId is { } ri)
                    {
                        if (rfeil is null) await kjoring.FullfortAsync(ri, oppdatert);
                        else await kjoring.FeiletAsync(ri, rfeil);
                    }
                    if (rfeil is null) _sisteReparasjon = naa;
                    if (oppdatert > 0) _log.LogInformation("GDPR-reparasjon: krypterte {N} rader (klartekst/HMAC).", oppdatert);
                }

                // ---- Alarm 2: fødselsnummer i klartekst (etter ev. reparasjon) ----
                var klartekst = await kunder.AntallKlartekstFnrAsync();
                if (klartekst > 0)
                {
                    await alarm.RaiseAsync("Kryptering", "Fødselsnummer i klartekst oppdaget",
                        $"{klartekst} kundekort har fødselsnummer i KLARTEKST (ikke «enc:1:»). Sett Gdpr__FieldKey — reparasjonsjobben krypterer dem automatisk når nøkkelen er på plass. Gå opp avviket.",
                        AlarmService.Alvorlighet.Kritisk, "Kryptering", "gdpr-klartekst-fnr");
                }
                else if (klartekst == 0)
                {
                    await alarm.LosOppNoekkelAsync("gdpr-klartekst-fnr", "System (ingen klartekst)");
                }

                // ---- Alarm 3: rader over oppbevaringstid som IKKE er anonymisert (måler at jobben VIRKER) ----
                // Alarm 1 måler at jobben kjører; denne fanger at den kan fullføre uten å behandle radene
                // den skulle (feil parameter/terskel/filter). 48-timers karens gir jobben tid til å ta unna.
                var anonMnd = await settings.GetIntAsync("gdpr_anonymize_months", 12);
                var ikkeAnon = await kunder.TellIkkeAnonymisertOverTidAsync(anonMnd);
                if (ikkeAnon > 0)
                {
                    var sidenStr = await settings.GetAsync(KeyOverSiden);
                    if (!DateTime.TryParse(sidenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var siden))
                    {
                        siden = naa;
                        await settings.SetAsync(KeyOverSiden, naa.ToString("o"));   // marker start på tilstanden
                    }
                    if (naa - siden > FullfortGrense)
                    {
                        await alarm.RaiseAsync("GDPR", "Rader over oppbevaringstid ikke anonymisert",
                            $"{ikkeAnon} kundekort er over oppbevaringstiden og ikke anonymisert.",
                            AlarmService.Alvorlighet.Kritisk, "GDPR-jobb", "gdpr-over-oppbevaring");
                    }
                }
                else if (ikkeAnon == 0)
                {
                    await settings.SetAsync(KeyOverSiden, "");   // nullstill karens-vindu
                    await alarm.LosOppNoekkelAsync("gdpr-over-oppbevaring", "System (0 over oppbevaringstid)");
                }
                // ikkeAnon == -1 (feil under telling): la ev. eksisterende alarm stå urørt.
            }
            catch (Exception ex) { _log.LogError(ex, "GDPR-overvåkingssyklus feilet"); }

            try { await Task.Delay(Intervall, ct); }
            catch { break; }
        }
    }
}
