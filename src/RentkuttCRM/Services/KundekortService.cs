namespace RentkuttCRM.Services;

/// <summary>
/// Lagring og henting av kundekort (lånesøknader) mot Supabase.
/// Staging-fallback (in-memory) når Supabase ikke er konfigurert.
/// </summary>
public class KundekortService
{
    public static readonly string[] Statuser =
        { "Nytt lead", "Påbegynt søknad", "Åpen", "Pågår", "Manuell behandling", "Sendt bank", "Sendt til bank - Timeout", "Feilet i sending", "Tilbud utsendt", "Fullført og utbetalt", "Avslått" };
    /// <summary>Nytt, ueid lead (f.eks. fra Prismatch) som ikke er plukket/behandlet ennå.</summary>
    public const string StatusNyttLead = "Nytt lead";
    /// <summary>Utkast opprettet fra Vipps/BankID-bekreftelse, før kunden har fullført skjemaet.</summary>
    public const string StatusPaabegynt = "Påbegynt søknad";
    public const string StatusAapen = "Åpen";
    public const string StatusFullfort = "Fullført og utbetalt";
    public const string StatusAvslatt = "Avslått";
    public const string StatusSendtBank = "Sendt bank";
    public const string StatusSendtBankTimeout = "Sendt til bank - Timeout";
    public const string StatusTilbudUtsendt = "Tilbud utsendt";
    public const string StatusFeiletSending = "Feilet i sending";

    // Innstilling: antall dager en sak kan stå i «Sendt bank» før den auto-settes til timeout. 0 = av.
    public const string KeySendtBankTimeoutDager = "sendt_bank_timeout_dager";

    /// <summary>Forenklet status for tredjeparter: åpen / utbetalt / avslått + om saken er ferdigbehandlet.</summary>
    public static (string kode, string tekst, bool ferdig) TredjepartStatus(string? status) => status switch
    {
        StatusFullfort => ("utbetalt", "Utbetalt", true),
        StatusAvslatt => ("avslatt", "Avslått", true),
        _ => ("apen", "Åpen", false),
    };

    /// <summary>Kilde/sikkerhetsnivå for fødselsnummeret på saken (dokumenterer hvordan identiteten
    /// ble fastslått): BankID (høyest), Vipps, eller Skjema (kunden oppga selv i skjema).</summary>
    public static readonly string[] FnrKilder = { "BankID", "Vipps", "Skjema" };
    public const string FnrKildeVipps = "Vipps";
    public const string FnrKildeBankId = "BankID";
    public const string FnrKildeSkjema = "Skjema";

    /// <summary>Rettslig grunnlag (GDPR art. 6). «Samtykke» er standard for innkommende leads.</summary>
    public static readonly string[] Behandlingsgrunnlag =
        { "Samtykke", "Avtale", "Rettslig forpliktelse", "Berettiget interesse" };
    public const string BehandlingsgrunnlagStandard = "Samtykke";

    public static readonly string[] Laanetyper = { "Forbrukslån", "Refinansiering", "Boliglån" };
    public static readonly string[] Sivilstatuser = { "Singel", "Samboer", "Gift", "Skilt", "Separert", "Enke(mann)" };
    public static readonly string[] Boforhold = { "Selveier/enebolig", "Andel/borettslag", "Leier", "Hos foreldre" };
    public static readonly string[] Arbeidssituasjoner =
        { "Fast ansatt", "Selvstendig næringsdrivende", "Offentlig sektor", "Pensjonist", "Arbeidsledig", "Uføretrygdet", "Hjemmeværende", "Student" };

    /// <summary>CSS-klasse for leadskilde-badge (rentekutt = hvit, prismatch = lys grønn).</summary>
    public static string KildeBadgeKlasse(string? kilde)
    {
        var k = (kilde ?? "").ToLowerInvariant();
        if (k.Contains("prismatch")) return "kilde-prismatch";
        if (k.Contains("rentekutt")) return "kilde-rentekutt";
        return "kilde-noytral";
    }

    private readonly Supabase.Client _client;
    private readonly PostnummerService _postnr;
    private readonly LoggService _logg;
    private readonly CryptoService _krypto;
    private readonly AlarmService _alarm;
    private readonly DatabaseMigrator _migrator;
    private readonly ILogger<KundekortService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Kundekort> _staging = new();
    private bool _initialized;

    public KundekortService(Supabase.Client client, PostnummerService postnr, LoggService logg, CryptoService krypto, AlarmService alarm, DatabaseMigrator migrator, IConfiguration cfg, ILogger<KundekortService> log)
    {
        _client = client;
        _postnr = postnr;
        _logg = logg;
        _krypto = krypto;
        _alarm = alarm;
        _migrator = migrator;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    // Selvhelbredende mot PGRST204/205: PostgREST kjenner ikke en kolonne/tabell som finnes i DB
    // (stale skjema-cache, typisk rett etter en migrasjon). Ber PostgREST reloade og prøver på nytt,
    // så innkommende payloads ikke feiler på en fersk kolonne.
    private static bool ErSkjemaCacheFeil(Exception ex) =>
        ex.Message.Contains("PGRST204", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("PGRST205", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("schema cache", StringComparison.OrdinalIgnoreCase);

    private async Task<T> MedSkjemaRetry<T>(Func<Task<T>> op)
    {
        // Skjema-cache-feil (PGRST204/205) er trygg å prøve på nytt — skrivingen ble AVVIST, aldri
        // utført, så ingen fare for dublett. Supabase kan kjøre flere PostgREST-instanser med ujevn
        // cache; vi ber om reload og prøver på nytt med økende pause til en frisk instans svarer.
        const int maksForsok = 5;
        for (var forsok = 1; ; forsok++)
        {
            try { return await op(); }
            catch (Exception ex) when (ErSkjemaCacheFeil(ex) && forsok < maksForsok)
            {
                _log.LogWarning(ex, "PostgREST skjema-cache-feil (forsøk {Forsok}/{Maks}) — reload + retry", forsok, maksForsok);
                await _migrator.ReloadSchemaCacheAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(800 * forsok));  // 0.8s, 1.6s, 2.4s, 3.2s
            }
        }
    }

    // ---- Feltnivåkryptering (fødselsnummer m.m.) ----
    // Appen jobber alltid i klartekst; kryptering skjer kun i det vi skriver til / leser
    // fra databasen. Krypter-ved-skriving skjer på en KLON (ForDb), så objektet kalleren
    // holder forblir i klartekst. Dekryptering skjer på alle leseveier (Avdekk).

    /// <summary>Lag en kryptert DB-variant (fnr/medsøker-fnr/kunde_id) + søkbar HMAC. Rører ikke originalen.</summary>
    private Kundekort ForDb(Kundekort k)
    {
        var db = k.Klon();
        db.FnrHmac = _krypto.HmacFnr(k.Foedselsnummer) ?? _krypto.HmacFnr(k.KundeId);
        db.Foedselsnummer = _krypto.Beskytt(k.Foedselsnummer);
        db.MedsokerFoedselsnummer = _krypto.Beskytt(k.MedsokerFoedselsnummer);
        db.KundeId = _krypto.Beskytt(k.KundeId) ?? "";
        return db;
    }

    /// <summary>Har kortet et fødselsnummer som må beskyttes? (søker, medsøker, eller kunde_id som er fnr).</summary>
    private static bool HarFodselsnummer(Kundekort k)
    {
        static bool ErFnr(string? s) => !string.IsNullOrWhiteSpace(s) && new string(s.Where(char.IsDigit).ToArray()).Length == 11;
        return !string.IsNullOrWhiteSpace(k.Foedselsnummer)
            || !string.IsNullOrWhiteSpace(k.MedsokerFoedselsnummer)
            || (k.KundeType == "B2C" && ErFnr(k.KundeId));
    }

    /// <summary>Dekrypter personopplysninger på et kundekort lest fra databasen.</summary>
    private void Avdekk(Kundekort? k)
    {
        if (k is null) return;
        k.Foedselsnummer = _krypto.Avdekk(k.Foedselsnummer);
        k.MedsokerFoedselsnummer = _krypto.Avdekk(k.MedsokerFoedselsnummer);
        k.KundeId = _krypto.Avdekk(k.KundeId) ?? "";
    }

    private List<Kundekort> AvdekkAlle(List<Kundekort> liste)
    {
        foreach (var k in liste) Avdekk(k);
        return liste;
    }

    // Menneskelesbare endringer mellom to versjoner av et kundekort (til logg).
    private static IEnumerable<string> Endringer(Kundekort a, Kundekort b)
    {
        string kr(decimal? v) => v.HasValue ? $"{v:N0} kr" : "—";
        string t(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
        string i(int? v) => v.HasValue ? v.Value.ToString() : "—";
        // Masker sensitive identifikatorer i revisjonssporet — vis kun de siste 4 sifrene,
        // så en endring er synlig uten at fødselsnummer havner i klartekst i loggen.
        string mask(string? v)
        {
            var dg = new string((v ?? "").Where(char.IsDigit).ToArray());
            return dg.Length == 0 ? "—" : new string('*', Math.Max(0, dg.Length - 4)) + dg[^Math.Min(4, dg.Length)..];
        }
        var d = new List<string>();
        void C(string felt, string fra, string til) { if (fra != til) d.Add($"Endret {felt}: {fra} → {til}"); }
        C("kundetype", a.KundeType == "B2B" ? "Bedrift" : "Privat", b.KundeType == "B2B" ? "Bedrift" : "Privat");
        C("status", t(a.Status), t(b.Status));
        C("fullt navn", t(a.FulltNavn), t(b.FulltNavn));
        C("fødselsnummer", mask(a.Foedselsnummer), mask(b.Foedselsnummer));
        C("orgnr", t(a.Orgnr), t(b.Orgnr));
        C("mobilnummer", t(a.Mobilnummer), t(b.Mobilnummer));
        C("e-post", t(a.Epost), t(b.Epost));
        C("adresse", t(a.Adresse), t(b.Adresse));
        C("postnummer", t(a.Postnummer), t(b.Postnummer));
        C("poststed", t(a.Poststed), t(b.Poststed));
        C("lånetype", t(a.Laanetype), t(b.Laanetype));
        C("ønsket lånebeløp", kr(a.OnsketLaanebelop), kr(b.OnsketLaanebelop));
        C("løpetid (mnd)", i(a.OnsketLopetidMnd), i(b.OnsketLopetidMnd));
        C("låneformål", t(a.Laaneformal), t(b.Laaneformal));
        C("nåværende bank", t(a.NavarendeBank), t(b.NavarendeBank));
        C("nåværende rente", a.NaavaerendeRente?.ToString() ?? "—", b.NaavaerendeRente?.ToString() ?? "—");
        C("boligverdi", kr(a.Boligverdi), kr(b.Boligverdi));
        C("årsinntekt", kr(a.AarsinntektBrutto), kr(b.AarsinntektBrutto));
        C("boliggjeld", kr(a.Boliggjeld), kr(b.Boliggjeld));
        C("forbruksgjeld", kr(a.Forbruksgjeld), kr(b.Forbruksgjeld));
        C("delegert bank", t(a.DelegertBank), t(b.DelegertBank));
        C("behandlingsgrunnlag", t(a.Behandlingsgrunnlag), t(b.Behandlingsgrunnlag));
        return d;
    }

    /// <summary>Berik geografifelt (kommune, poststed, fylke) fra postnummer når de mangler.</summary>
    public void BerikGeografi(Kundekort k)
    {
        if (string.IsNullOrWhiteSpace(k.Postnummer)) return;
        if (string.IsNullOrWhiteSpace(k.Kommune)) k.Kommune = _postnr.Kommune(k.Postnummer) ?? k.Kommune;
        if (string.IsNullOrWhiteSpace(k.Poststed)) k.Poststed = _postnr.Poststed(k.Postnummer) ?? k.Poststed;
        if (string.IsNullOrWhiteSpace(k.Fylke)) k.Fylke = _postnr.Fylke(k.Postnummer) ?? k.Fylke;
    }

    /// <summary>Auto-fyll boliglån/eiendom-feltene (G) fra kundens eksisterende opplysninger når de er
    /// tomme — så agenten har et utgangspunkt. Kun der vi faktisk har data (kommune, boligverdi).
    /// Overskriver aldri felt agenten selv har fylt (skjer bare når feltet er tomt). Matrikkel/
    /// borettslag finnes ikke i leadet og fylles manuelt; adresse kan avvike fra bostedsadressen.</summary>
    private static void AutofyllEiendom(Kundekort k)
    {
        if (string.IsNullOrWhiteSpace(k.EiendomKommune) && !string.IsNullOrWhiteSpace(k.Kommune))
            k.EiendomKommune = k.Kommune;
        if ((k.EiendomEstimertVerdi ?? 0) <= 0 && k.Boligverdi is > 0)
            k.EiendomEstimertVerdi = k.Boligverdi;
    }

    /// <param name="strict">Når true (manuelt skjema) kreves korrekt fødselsnr/orgnr-lengde.
    /// Når false (API/webhook) opprettes saken uansett — id = fødselsnr → mobil → fallback.</param>
    public async Task<(bool ok, string? error)> SaveAsync(Kundekort k, bool strict = false, string? aktor = null)
    {
        k.KundeId = (k.KundeId ?? "").Trim();
        BerikGeografi(k);   // fyll kommune/poststed/fylke fra postnummer når de mangler
        AutofyllEiendom(k); // fyll G · Boliglån (kommune/estimert verdi) fra eksisterende felt
        if (string.IsNullOrWhiteSpace(k.Behandlingsgrunnlag)) k.Behandlingsgrunnlag = BehandlingsgrunnlagStandard;

        if (strict)
        {
            var expected = k.KundeType == "B2B" ? 9 : 11;
            if (k.KundeId.Length != expected)
                return (false, $"{(k.KundeType == "B2B" ? "Organisasjonsnummer" : "Fødselsnummer")} må være {expected} siffer.");
        }
        else if (string.IsNullOrWhiteSpace(k.KundeId))
        {
            // Verken fødselsnr eller mobil — opprett saken med en generert id.
            k.KundeId = "lead-" + Guid.NewGuid().ToString("N")[..12];
        }

        // Speil KUN et gyldig fødselsnummer (MOD11) fra kunde_id inn i fnr-feltet. For prismatch-leads
        // faller kunde_id tilbake til mobilnummer — og et mobilnummer skal ALDRI havne som fødselsnummer
        // (pnr skal være blankt). MOD11-sjekken sikrer at bare et ekte fnr speiles.
        if (k.KundeType == "B2C" && string.IsNullOrWhiteSpace(k.Foedselsnummer) && Fnr.ErGyldig(k.KundeId))
            k.Foedselsnummer = k.KundeId;

        // B2B: hold orgnr-feltet i synk med et gyldig 9-sifret kunde_id.
        if (k.KundeType == "B2B" && string.IsNullOrWhiteSpace(k.Orgnr) && k.KundeId.Length == 9 && k.KundeId.All(char.IsDigit))
            k.Orgnr = k.KundeId;

        // Fail-open: mangler krypteringsnøkkelen, lagres fødselsnummer i KLARTEKST (så leads ikke
        // går tapt), men vi reiser en alarm slik at tilstanden ikke er stille. Alarmen dedupliseres
        // på nøkkel, så den teller opp i stedet for å spamme.
        if (IsConfigured && !_krypto.IsEnabled && HarFodselsnummer(k))
        {
            try
            {
                await _alarm.RaiseAsync("Kryptering", "Fødselsnummer lagret i klartekst",
                    "Gdpr__FieldKey mangler — fødselsnummer lagres i klartekst. Sett nøkkelen og kjør bakfylling.",
                    AlarmService.Alvorlighet.Kritisk, "Kundekort", "kryptering-av-klartekst");
            }
            catch (Exception ex) { _log.LogWarning(ex, "Alarm (klartekst-fnr) feilet"); }
        }

        if (!IsConfigured)
        {
            if (k.Id == Guid.Empty) k.Id = Guid.NewGuid();
            _staging.RemoveAll(x => x.Id == k.Id);
            _staging.Add(k);
            return (true, null);
        }

        try
        {
            await EnsureReadyAsync();
            // Tom Id = ny sak (DB genererer id). Ellers oppdater eksisterende sak.
            if (k.Id == Guid.Empty)
            {
                var resp = await MedSkjemaRetry(() => _client.From<Kundekort>().Insert(ForDb(k)));
                InvaliderCache();
                var nyId = resp.Models.FirstOrDefault()?.Id ?? Guid.Empty;
                if (nyId != Guid.Empty)
                {
                    k.Id = nyId;   // så kaller (webhook m.fl.) kan knytte samtykke/relasjoner til id-en
                    await _logg.LoggAsync(nyId, aktor ?? k.Kilde,
                        $"Registrert kundekort{(string.IsNullOrWhiteSpace(k.Kilde) ? "" : $" ({k.Kilde})")}");
                }
            }
            else
            {
                var gammel = await GetAsync(k.Id);
                var resp = await MedSkjemaRetry(() => _client.From<Kundekort>().Update(ForDb(k)));
                // Avslør stille feil: en oppdatering som treffer 0 rader (RLS/ukjent id)
                // returnerer ingen modeller — ikke meld suksess da.
                if (resp.Models.Count == 0)
                    return (false, "Ingen rader oppdatert (mangler tilgang eller ukjent id?).");
                InvaliderCache();
                if (gammel is not null)
                {
                    var endr = Endringer(gammel, k).ToList();
                    if (endr.Count > 0) await _logg.LoggFlereAsync(k.Id, aktor, endr);
                }
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lagring av kundekort feilet");
            return (false, "Teknisk feil ved lagring: " + ex.Message);
        }
    }

    /// <summary>Live-test av Supabase-API-et (PostgREST). Bekrefter at REST-laget svarer på en lett
    /// spørring. Brukes i Admin for å vise API-kontakt uavhengig av den direkte Postgres-tilkoblingen.</summary>
    public async Task<(bool ok, string detalj)> TestApiAsync()
    {
        if (!IsConfigured) return (false, "Supabase:Url / Supabase:Key er ikke satt.");
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Select("id").Limit(1).Get();
            return (true, "Tilkoblet (Supabase API / PostgREST).");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---- Kort-levetids cache for hele lista ----
    // Blazor Server holder circuit-scopet i live på tvers av sidenavigering, så
    // gjentatte sidelastinger (CRM → Marked → Oppfølging → tilbake) gjenbruker
    // lista i stedet for å hente hele kundekort-tabellen på nytt hver gang —
    // hovedårsaken til at portalen føltes treg ved navigering. Cachen invalideres
    // ved enhver skriving (InvaliderCache), og utløper uansett etter kort tid slik
    // at endringer fra andre brukere/webhooks kommer inn raskt.
    private List<Kundekort>? _listeCache;
    private DateTime _listeCacheTid;
    private List<Kundekort>? _lettCache;
    private DateTime _lettCacheTid;
    private static readonly TimeSpan CacheLevetid = TimeSpan.FromSeconds(15);

    private void InvaliderCache() { _listeCache = null; _lettCache = null; }

    private async Task<List<Kundekort>> HentAlleAsync()
    {
        if (_listeCache is not null && DateTime.UtcNow - _listeCacheTid < CacheLevetid)
            return _listeCache;
        await EnsureReadyAsync();
        _listeCache = AvdekkAlle((await _client.From<Kundekort>().Get()).Models);
        _listeCacheTid = DateTime.UtcNow;
        return _listeCache;
    }

    // Kolonner list-/dashboardsidene (Marked, Oppfølging, CRM, Statistikk,
    // Markedsinnsikt) faktisk leser. Tunge/ubrukte felt hentes ikke — det store
    // notater-tekstfeltet, medsøker-blokken, kode-varianter, tjeneste/samtykke og
    // detaljert husholdning/gjeld — så radene blir ~halvparten så brede.
    // Database + tredjepart-API bruker fulle rader (notater/Beregn) og går via HentAlleAsync.
    private const string LetteKolonner =
        "id,kunde_id,kunde_type,orgnr,fullt_navn,foedselsnummer,mobilnummer,epost," +
        "adresse,postnummer,poststed,kommune,fylke,laanetype,produktkategori,laaneformal," +
        "onsket_laanebelop,onsket_lopetid_mnd,aarsinntekt_brutto,sivilstatus," +
        "arbeidssituasjon,boforhold,naavaerende_rente,navarende_bank,boliggjeld," +
        "boligverdi,status,eier,eier_navn,eier_tatt_at,delegert_bank,kilde," +
        "siste_kontakt,neste_oppfolging,sendt_bank_at,created_at,updated_at";

    private async Task<List<Kundekort>> HentAlleLettAsync()
    {
        if (_lettCache is not null && DateTime.UtcNow - _lettCacheTid < CacheLevetid)
            return _lettCache;
        await EnsureReadyAsync();
        _lettCache = AvdekkAlle((await _client.From<Kundekort>().Select(LetteKolonner).Get()).Models);
        _lettCacheTid = DateTime.UtcNow;
        return _lettCache;
    }

    public async Task<List<Kundekort>> ListAsync()
    {
        if (!IsConfigured) return _staging.ToList();
        try
        {
            return (await HentAlleAsync()).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort feilet");
            return new List<Kundekort>();
        }
    }

    /// <summary>Som <see cref="ListAsync"/>, men henter kun kolonnene list-/dashboardsidene
    /// bruker (uten notater/medsøker/detaljfelt). Bruk der fulle kort ikke trengs.</summary>
    public async Task<List<Kundekort>> ListLettAsync()
    {
        if (!IsConfigured) return _staging.ToList();
        try
        {
            return (await HentAlleLettAsync()).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort (lett) feilet");
            return new List<Kundekort>();
        }
    }

    /// <summary>Saker eid av en bruker (Mine oppfølginger).</summary>
    public async Task<List<Kundekort>> ListByEierAsync(string eier)
    {
        if (string.IsNullOrWhiteSpace(eier)) return new();
        if (!IsConfigured) return _staging.Where(k => k.Eier == eier).ToList();
        try
        {
            return (await HentAlleLettAsync()).Where(k => k.Eier == eier).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av egne saker feilet");
            return new();
        }
    }

    /// <summary>Ta eierskap til en sak. <paramref name="nyStatus"/> settes samtidig hvis oppgitt
    /// (brukes til å flytte «Åpen» → «Pågår» når en rådgiver overtar saken).</summary>
    public async Task SetEierAsync(Guid id, string eier, string eierNavn, string? nyStatus = null)
    {
        var naa = DateTime.UtcNow;
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) { k.Eier = eier; k.EierNavn = eierNavn; k.EierTattAt = naa; if (nyStatus is not null) { k.Status = nyStatus; if (nyStatus == StatusSendtBank) k.SendtBankAt = naa; } }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            var q = _client.From<Kundekort>()
                .Where(x => x.Id == id)
                .Set(x => x.Eier!, eier)
                .Set(x => x.EierNavn!, eierNavn)
                .Set(x => x.EierTattAt!, naa);
            if (nyStatus is not null) q = q.Set(x => x.Status, nyStatus);
            if (nyStatus == StatusSendtBank) q = q.Set(x => x.SendtBankAt!, naa);
            await q.Update();
            InvaliderCache();
            await _logg.LoggAsync(id, eierNavn, "Tok eierskap" + (nyStatus is not null ? $" · status → {nyStatus}" : ""));
        }
        catch (Exception ex) { _log.LogError(ex, "Sette eier feilet"); }
    }

    /// <summary>Gi fra seg eierskap (saken går tilbake til den utatte poolen).</summary>
    public async Task ReleaseEierAsync(Guid id)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) { k.Eier = null; k.EierNavn = null; k.EierTattAt = null; }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>()
                .Where(x => x.Id == id)
                .Set(x => x.Eier!, (string?)null)
                .Set(x => x.EierNavn!, (string?)null)
                .Set(x => x.EierTattAt!, (DateTime?)null)
                .Update();
            InvaliderCache();
        }
        catch (Exception ex) { _log.LogError(ex, "Frigi eierskap feilet"); }
    }

    /// <summary>Registrer at kunden ble kontaktet nå (nullstiller «tid siden siste kontakt»).</summary>
    public async Task RegistrerKontaktAsync(Guid id, DateTime naa)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.SisteKontakt = naa;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.SisteKontakt!, naa).Update();
            InvaliderCache();
        }
        catch (Exception ex) { _log.LogError(ex, "Registrering av kontakt feilet"); }
    }

    /// <summary>Registrer/oppdater kundens nåværende bank og rente (markedsinnsikt).</summary>
    public async Task SetBankRenteAsync(Guid id, string? bank, decimal? rente)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) { k.NavarendeBank = bank; k.NaavaerendeRente = rente; }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>()
                .Where(x => x.Id == id)
                .Set(x => x.NavarendeBank!, bank)
                .Set(x => x.NaavaerendeRente!, rente)
                .Update();
            InvaliderCache();
        }
        catch (Exception ex) { _log.LogError(ex, "Lagring av bank/rente feilet"); }
    }

    /// <summary>Sett (eller nullstill) neste planlagte oppfølging.</summary>
    public async Task SetNesteOppfolgingAsync(Guid id, DateTime? neste)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.NesteOppfolging = neste;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.NesteOppfolging!, neste).Update();
            InvaliderCache();
        }
        catch (Exception ex) { _log.LogError(ex, "Lagring av neste oppfølging feilet"); }
    }

    /// <summary>Nyeste sak som matcher et mobilnummer (siffer-normalisert). Brukes av tredjepart-API-et.</summary>
    public async Task<Kundekort?> FindByMobilAsync(string? mobil)
    {
        var digits = new string((mobil ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        // Match de siste 8 sifrene (håndterer +47 / landkode-varianter).
        var tail = digits[^8..];
        var alle = await ListAsync();
        return alle
            .Where(k =>
            {
                var m = new string((k.Mobilnummer ?? "").Where(char.IsDigit).ToArray());
                var id = new string((k.KundeId ?? "").Where(char.IsDigit).ToArray());
                return m.EndsWith(tail) || id.EndsWith(tail);
            })
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>Finn et påbegynt utkast (Vipps) som matcher en innkommende søknad.
    /// KUN entydige kriterier: 1) mobilnummer (siste 8 sifre), 2) e-post. Navn brukes IKKE
    /// som matchkriterium (ikke entydig — kunne koblet en Vipps-sesjon til feil persons søknad).
    /// Returnerer utkastet og hvilket felt som matchet (for revisjonssporet).</summary>
    public async Task<(Kundekort? utkast, string? felt)> FinnPaabegyntAsync(string? mobil, string? epost)
    {
        var utkast = (await ListAsync()).Where(k => k.Status == StatusPaabegynt).ToList();
        if (utkast.Count == 0) return (null, null);

        // 1) Mobilnummer — match på de siste 8 sifrene (tåler +47/landkode).
        var mDig = new string((mobil ?? "").Where(char.IsDigit).ToArray());
        if (mDig.Length >= 8)
        {
            var tail = mDig[^8..];
            var m = utkast.FirstOrDefault(k =>
            {
                var d = new string((k.Mobilnummer ?? "").Where(char.IsDigit).ToArray());
                return d.Length >= 8 && d[^8..] == tail;
            });
            if (m is not null) return (m, "mobilnummer");
        }

        // 2) E-post (uten hensyn til store/små bokstaver).
        if (!string.IsNullOrWhiteSpace(epost))
        {
            var e = utkast.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k.Epost)
                && string.Equals(k.Epost.Trim(), epost.Trim(), StringComparison.OrdinalIgnoreCase));
            if (e is not null) return (e, "e-post");
        }

        // Ingen entydig match → ingen kobling (utkastet blir stående som «Påbegynt søknad»).
        return (null, null);
    }

    public async Task SetStatusAsync(Guid id, string status, string? aktor = null)
    {
        // Når en sak settes til «Sendt bank», stemple tidspunktet — brukes av timeout-jobben.
        var settSendtBank = status == StatusSendtBank;
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) { k.Status = status; if (settSendtBank) k.SendtBankAt = DateTime.UtcNow; }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            var q = _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.Status, status);
            if (settSendtBank) q = q.Set(x => x.SendtBankAt!, DateTime.UtcNow);
            await q.Update();
            InvaliderCache();
            await _logg.LoggAsync(id, aktor, $"Endret status til {status}");
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av status feilet"); }
    }

    public async Task<(bool ok, string? error)> DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return (true, null); }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Delete();
            InvaliderCache();
            // Verifiser at raden faktisk er borte. Delete kaster ikke om 0 rader ble
            // truffet (f.eks. stille RLS-filtrering) — uten denne sjekken navigerer
            // UI-et bort som om det gikk bra, og kortet «dukker opp igjen» i listene.
            if (await GetAsync(id) is not null)
                return (false, "Kortet ble ikke slettet — manglende tilgang?");
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sletting av kundekort feilet");
            return (false, "Teknisk feil ved sletting.");
        }
    }

    public async Task SetDelegertBankAsync(Guid id, string? bank)
    {
        bank = string.IsNullOrWhiteSpace(bank) ? null : bank;
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.DelegertBank = bank;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.DelegertBank!, bank ?? "").Update();
            InvaliderCache();
        }
        catch (Exception ex) { _log.LogError(ex, "Delegering til bank feilet"); }
    }

    public async Task<Kundekort?> GetAsync(Guid id)
    {
        if (!IsConfigured) return _staging.FirstOrDefault(x => x.Id == id);
        try
        {
            await EnsureReadyAsync();
            var k = await _client.From<Kundekort>().Where(x => x.Id == id).Single();
            Avdekk(k);
            return k;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort {Id} feilet", id);
            return null;
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }

    public bool KrypteringPaa => _krypto.IsEnabled;

    /// <summary>Tester om NÅVÆRENDE nøkkel faktisk kan dekryptere EKSISTERENDE lagrede fnr.
    /// Henter rå (ukryptert-lest) verdier og forsøker Avdekk manuelt. Hvis mange «kunne ikke»,
    /// er nøkkelverdien en annen enn den dataene ble kryptert med (ikke bare «nøkkel mangler»).</summary>
    public async Task<(int medFnr, int dekryptertOk, int kunneIkke)> DekrypteringstestAsync()
    {
        if (!IsConfigured) return (0, 0, 0);
        try
        {
            await EnsureReadyAsync();
            var raa = (await _client.From<Kundekort>().Select("id,foedselsnummer").Get()).Models
                      .Where(x => !string.IsNullOrWhiteSpace(x.Foedselsnummer)).ToList();
            int ok = 0, feil = 0;
            foreach (var r in raa)
            {
                if (!_krypto.ErBeskyttet(r.Foedselsnummer)) { ok++; continue; } // klartekst = «kan leses»
                var dek = _krypto.Avdekk(r.Foedselsnummer);
                if (dek is not null && !_krypto.ErBeskyttet(dek)
                    && new string(dek.Where(char.IsDigit).ToArray()).Length == 11) ok++;
                else feil++;
            }
            return (raa.Count, ok, feil);
        }
        catch (Exception ex) { _log.LogError(ex, "Dekrypteringstest feilet"); return (-1, 0, 0); }
    }

    /// <summary>Engangs-bakfylling: krypter fødselsnummer/kunde_id og sett søkbar HMAC på
    /// eksisterende rader som ennå ligger i klartekst (eller mangler HMAC). Idempotent.</summary>
    public async Task<(int oppdatert, int uendret, string? feil)> KrypterEksisterendeAsync()
    {
        if (!IsConfigured) return (0, 0, "Supabase er ikke konfigurert.");
        if (!_krypto.IsEnabled) return (0, 0, "Gdpr__FieldKey er ikke satt — kan ikke kryptere.");
        try
        {
            await EnsureReadyAsync();
            // Rå rader (uten dekryptering) — kan være en blanding av klartekst og kryptert.
            var alle = (await _client.From<Kundekort>().Get()).Models;
            int opp = 0, u = 0;
            foreach (var raw in alle)
            {
                var maaKrypteres =
                    (!string.IsNullOrEmpty(raw.Foedselsnummer) && !_krypto.ErBeskyttet(raw.Foedselsnummer)) ||
                    (!string.IsNullOrEmpty(raw.MedsokerFoedselsnummer) && !_krypto.ErBeskyttet(raw.MedsokerFoedselsnummer)) ||
                    (!string.IsNullOrEmpty(raw.KundeId) && !_krypto.ErBeskyttet(raw.KundeId)) ||
                    string.IsNullOrEmpty(raw.FnrHmac);
                if (!maaKrypteres) { u++; continue; }

                // Dekrypter det som allerede er kryptert, la klartekst stå — så bygger vi
                // en frisk kryptert variant (idempotent) og skriver tilbake.
                var klartekst = raw.Klon();
                Avdekk(klartekst);
                var db = ForDb(klartekst);
                db.Id = raw.Id;
                await _client.From<Kundekort>().Update(db);
                opp++;
            }
            InvaliderCache();
            _log.LogInformation("Bakfylling kryptering: {Opp} oppdatert, {U} allerede kryptert", opp, u);
            return (opp, u, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bakfylling av kryptering feilet");
            return (0, 0, ex.Message);
        }
    }

    /// <summary>Engangs-opprydding: blank fødselsnummer-feltet på leads der verdien IKKE er et gyldig
    /// fødselsnummer (MOD11), men er selve mobilnummeret — feilaktig speilet fra kunde_id på prismatch-
    /// leads. Kirurgisk: rører kun rader der fnr-sifrene er identiske med mobilnummeret, aldri et
    /// gyldig fnr eller andre skjeve verdier. Nullstiller også FnrHmac for disse. Returnerer antall.</summary>
    public async Task<(int ryddet, int sjekket, string? feil)> RyddMobilIFnrAsync()
    {
        if (!IsConfigured) return (0, 0, "Supabase er ikke konfigurert.");
        try
        {
            await EnsureReadyAsync();
            var alle = (await _client.From<Kundekort>().Get()).Models;
            int ryddet = 0, sjekket = 0;
            foreach (var raw in alle)
            {
                if (string.IsNullOrWhiteSpace(raw.Foedselsnummer)) continue;
                sjekket++;

                var fnr = _krypto.Avdekk(raw.Foedselsnummer);           // klartekst eller dekryptert
                if (string.IsNullOrWhiteSpace(fnr) || Fnr.ErGyldig(fnr)) continue;  // gyldig fnr → behold

                var fnrDigits = new string(fnr.Where(char.IsDigit).ToArray());
                var mobilDigits = new string((raw.Mobilnummer ?? "").Where(char.IsDigit).ToArray());
                // Kun rydd når «fnr» faktisk ER mobilnummeret — ikke andre ugyldige verdier.
                if (fnrDigits.Length == 0 || fnrDigits != mobilDigits) continue;

                raw.Foedselsnummer = null;
                raw.FnrHmac = null;
                await _client.From<Kundekort>().Update(raw);
                ryddet++;
            }
            InvaliderCache();
            _log.LogInformation("Opprydding mobil-i-fnr: {R} ryddet av {S} med fnr-verdi", ryddet, sjekket);
            return (ryddet, sjekket, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opprydding mobil-i-fnr feilet");
            return (0, 0, ex.Message);
        }
    }
}
