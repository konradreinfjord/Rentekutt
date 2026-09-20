namespace RentkuttCRM.Services;

/// <summary>
/// Auto-send av uferdige søknader: sender lead som fortsatt står i status «Påbegynt søknad»
/// til bank(er) automatisk ~30 minutter etter registrering, dersom
///   (1) leadet matcher logikk-matrisen for banken, OG
///   (2) banken har bryteren «Send uferdige etter 30 min» (auto_paabegynt) PÅ.
/// Sendingen legges i den vanlige sendekøen (BankSendWorker), som håndterer throttling,
/// samtykke-sperre (Instabank) og webhook-levering (Nextcom).
///
/// Sikkerhet:
///  • KUN produksjon — dev/lokale instanser peker på prod-Supabase og skal aldri auto-sende.
///  • Aktiveringssperre: kun leads registrert ETTER at funksjonen ble aktivert sendes
///    («ikke send gamle søknader, kun nye som kommer inn»).
///  • Dedup: sender ikke til en bank leadet allerede er kølagt/sendt til, og flytter leadet
///    ut av «Påbegynt søknad» ved kølegging, så det ikke plukkes to ganger.
/// </summary>
public class AutoPaabegyntWorker : BackgroundService
{
    // Hvor lenge et lead må ha stått i «Påbegynt søknad» før auto-sending (minutter).
    public const string KeyMinMinutter = "auto_paabegynt_min_minutter";
    public const int StandardMinMinutter = 30;
    // Tidspunktet funksjonen ble aktivert (ISO-8601, UTC). Settes ved første kjøring; kun leads
    // registrert etter dette sendes. Garanterer at eksisterende «gamle» leads aldri auto-sendes.
    public const string KeyAktivertFra = "auto_paabegynt_aktivert_fra";

    private static readonly TimeSpan Intervall = TimeSpan.FromMinutes(5);
    // Sikkerhetstak: send aldri til søknader eldre enn dette (belte og bukseseler ved siden av
    // aktiveringssperra), så et lead som av en eller annen grunn blir stående ikke sendes uker senere.
    private static readonly TimeSpan MaksAlder = TimeSpan.FromDays(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoPaabegyntWorker> _log;
    private readonly IHostEnvironment _env;

    public AutoPaabegyntWorker(IServiceScopeFactory scopeFactory, ILogger<AutoPaabegyntWorker> log, IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Kun produksjon skal auto-sende leads til bank. Dev peker på prod-Supabase men skal aldri sende.
        if (!_env.IsProduction()) return;

        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await KjorSyklusAsync(scope, ct);
            }
            catch (Exception ex) { _log.LogError(ex, "Auto-påbegynt-syklus feilet"); }

            try { await Task.Delay(Intervall, ct); } catch { break; }
        }
    }

    private async Task KjorSyklusAsync(IServiceScope scope, CancellationToken ct)
    {
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var partnereSvc = scope.ServiceProvider.GetRequiredService<PartnerService>();

        // Ingen banker har bryteren på ⇒ ingenting å gjøre.
        var partnere = await partnereSvc.ListAsync();
        var autoBanker = partnere.Where(p => p.AutoPaabegynt).ToList();
        if (autoBanker.Count == 0) return;

        // Aktiveringssperre: første gang funksjonen faktisk er i bruk, forankre «fra og med nå».
        // Kun leads registrert etter dette tidspunktet er kandidater — gamle leads sendes aldri.
        var aktivertFra = await LesAktivertFraAsync(settings);
        if (aktivertFra is null)
        {
            await settings.SetAsync(KeyAktivertFra, DateTime.UtcNow.ToString("O"));
            _log.LogInformation("Auto-påbegynt: aktivert nå — kun nye leads etter dette sendes.");
            return; // denne syklusen sender ingenting (ingen leads er nyere enn «nå» ennå)
        }

        var minMinutter = Math.Max(1, await settings.GetIntAsync(KeyMinMinutter, StandardMinMinutter));
        var naa = DateTime.UtcNow;
        var oevreGrense = naa.AddMinutes(-minMinutter);       // minst så gammelt
        var nedreGrense = naa - MaksAlder;                    // men ikke eldre enn maks
        var nedreEffektiv = aktivertFra.Value > nedreGrense ? aktivertFra.Value : nedreGrense;

        var kundekort = scope.ServiceProvider.GetRequiredService<KundekortService>();
        var kandidater = (await kundekort.ListLettAsync()).Where(k =>
            k.Status == KundekortService.StatusPaabegynt &&
            k.CreatedAt <= oevreGrense &&
            k.CreatedAt >= nedreEffektiv).ToList();
        if (kandidater.Count == 0) return;

        var regler = await scope.ServiceProvider.GetRequiredService<RutingsregelService>().ListAsync();
        var ko = scope.ServiceProvider.GetRequiredService<BankSendingService>();
        var produkter = await scope.ServiceProvider.GetRequiredService<PartnerProduktService>().ListAsync();
        var samtykke = scope.ServiceProvider.GetRequiredService<SamtykkeService>();
        var instabank = scope.ServiceProvider.GetRequiredService<InstabankService>();
        var logg = scope.ServiceProvider.GetRequiredService<LoggService>();

        foreach (var lett in kandidater)
        {
            if (ct.IsCancellationRequested) break;

            // Full henting: rutingsreglene og produktvalget trenger alle felt (og dekryptert fnr).
            var k = await kundekort.GetAsync(lett.Id);
            if (k is null || k.Status != KundekortService.StatusPaabegynt) continue;

            // Banker leadet matcher i logikk-matrisen, avgrenset til de med bryteren PÅ.
            var matchende = RutingEval.MatchendeBanker(regler, k)
                .Where(navn => autoBanker.Any(b => string.Equals(b.Navn, navn, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matchende.Count == 0) continue;

            // Dedup: hopp over banker leadet allerede er kølagt/sendt til.
            var eksisterende = (await ko.ForKundeAsync(k.Id))
                .Select(s => s.Bank).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var kølagt = new List<string>();
            foreach (var bankNavn in matchende)
            {
                if (eksisterende.Contains(bankNavn)) continue;

                var partner = autoBanker.First(b => string.Equals(b.Navn, bankNavn, StringComparison.OrdinalIgnoreCase));
                var (klar, produkt, kode, hopp) = ForberedSending(partner, k, produkter, instabank);
                if (!klar) { if (hopp is not null) _log.LogInformation("Auto-påbegynt: hopper over {Bank} for {Id}: {Grunn}", bankNavn, k.Id, hopp); continue; }

                // Instabank krever gyldig samtykke — uten det ville sendekøen feilmarkere leadet.
                // Vi sender heller ikke (leadet blir stående til samtykke ev. kommer).
                if (InstabankService.ErInstabankNavn(bankNavn))
                {
                    var harSamtykke = await samtykke.HarGyldigEllerLegacyAsync(k.Id, SamtykkeService.FormaalKreditt, k.SamtykkeGjeldsregisterKredittsjekk);
                    if (!harSamtykke) { _log.LogInformation("Auto-påbegynt: {Id} mangler samtykke for {Bank} — ikke sendt.", k.Id, bankNavn); continue; }
                }

                var s = new BankSending
                {
                    KundekortId = k.Id,
                    KundeNavn = k.FulltNavn,
                    Bank = bankNavn,
                    Produkt = produkt,
                    ProduktKode = kode,
                    SendtAv = "System (auto 30 min)",
                    Status = SendStatus.IKo,
                    Detalj = "Lagt i sendekø automatisk (uferdig søknad etter 30 min).",
                };
                var (_, feil) = await ko.LoggAsync(s);
                if (feil is not null) { _log.LogWarning("Auto-påbegynt: kunne ikke kølegge {Bank} for {Id}: {Feil}", bankNavn, k.Id, feil); continue; }

                await logg.LoggAsync(k.Id, "System (auto 30 min)",
                    $"Auto-sendt til sendekø etter 30 min uten fullføring: {bankNavn}{(produkt is null ? "" : $" · {produkt}")}",
                    kategori: "avgjørelse",
                    begrunnelse: "Automatisk ruting (uferdig søknad, logikk-matrise + bank-bryter)");
                kølagt.Add(bankNavn);
            }

            // Kølagt til minst én bank ⇒ flytt ut av «Påbegynt søknad» så leadet ikke plukkes på nytt.
            if (kølagt.Count > 0)
            {
                await kundekort.SetStatusAsync(k.Id, KundekortService.StatusSendtIProsess, "System (auto 30 min)");
                _log.LogInformation("Auto-påbegynt: {Id} auto-sendt til {Banker}.", k.Id, string.Join(", ", kølagt));
            }
        }
    }

    // Avgjør om leadet kan sendes til banken og hvilket produkt/kode som skal brukes.
    // Speiler den manuelle «Send til matchet bank»-logikken (segment + lånetype → produkt).
    private static (bool Klar, string? Produkt, int? Kode, string? Hopp) ForberedSending(
        Partner partner, Kundekort k, List<PartnerProdukt> alleProdukter, InstabankService instabank)
    {
        // Webhook-/manuelle banker (ikke Instabank): ingen produktkode nødvendig.
        if (!InstabankService.ErInstabankNavn(partner.Navn))
            return (true, null, null, null);

        // Instabank: velg produkt ut fra segment + lånetype (nøyaktig ett treff), ellers første.
        var segment = PartnerProdukt.SegmentFor(k.KundeType);
        var forBank = alleProdukter
            .Where(p => p.PartnerId == partner.Id && p.Aktiv
                        && string.Equals(p.Segment, segment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Sortering).ThenBy(p => p.Navn).ToList();
        if (forBank.Count == 0) return (false, null, null, "ingen aktive produkter for segmentet");

        var etterLaanetype = forBank.Where(p => p.GjelderLaanetype(k.Laanetype)).ToList();
        var valgt = etterLaanetype.Count == 1 ? etterLaanetype[0] : forBank[0];

        // Beløpsbarriere: overstiger ønsket beløp maksgrensen for produktet, ikke send auto.
        var maks = instabank.MaksBelopFor(valgt.Kode);
        if (maks > 0 && (k.OnsketLaanebelop ?? 0) > maks)
            return (false, null, null, $"beløp {k.OnsketLaanebelop:N0} over maks {maks:N0} for {valgt.Navn}");

        return (true, valgt.Navn, valgt.Kode, null);
    }

    private static async Task<DateTime?> LesAktivertFraAsync(SettingsService settings)
    {
        var raw = await settings.GetAsync(KeyAktivertFra);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime() : (DateTime?)null;
    }
}
