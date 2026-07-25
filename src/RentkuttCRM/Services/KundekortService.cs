namespace RentkuttCRM.Services;

/// <summary>
/// Lagring og henting av kundekort (lånesøknader) mot Supabase.
/// Staging-fallback (in-memory) når Supabase ikke er konfigurert.
/// </summary>
public class KundekortService
{
    public static readonly string[] Statuser =
        { "Åpen", "Pågår", "Manuell behandling", "Sendt bank", "Feilet i sending", "Tilbud utsendt", "Fullført og utbetalt", "Avslått" };
    public const string StatusFullfort = "Fullført og utbetalt";
    public const string StatusAvslatt = "Avslått";
    public const string StatusSendtBank = "Sendt bank";
    public const string StatusFeiletSending = "Feilet i sending";

    /// <summary>Forenklet status for tredjeparter: åpen / utbetalt / avslått + om saken er ferdigbehandlet.</summary>
    public static (string kode, string tekst, bool ferdig) TredjepartStatus(string? status) => status switch
    {
        StatusFullfort => ("utbetalt", "Utbetalt", true),
        StatusAvslatt => ("avslatt", "Avslått", true),
        _ => ("apen", "Åpen", false),
    };

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
    private readonly ILogger<KundekortService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Kundekort> _staging = new();
    private bool _initialized;

    public KundekortService(Supabase.Client client, PostnummerService postnr, LoggService logg, CryptoService krypto, IConfiguration cfg, ILogger<KundekortService> log)
    {
        _client = client;
        _postnr = postnr;
        _logg = logg;
        _krypto = krypto;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
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
        var d = new List<string>();
        void C(string felt, string fra, string til) { if (fra != til) d.Add($"Endret {felt}: {fra} → {til}"); }
        C("kundetype", a.KundeType == "B2B" ? "Bedrift" : "Privat", b.KundeType == "B2B" ? "Bedrift" : "Privat");
        C("status", t(a.Status), t(b.Status));
        C("fullt navn", t(a.FulltNavn), t(b.FulltNavn));
        C("fødselsnummer", t(a.Foedselsnummer), t(b.Foedselsnummer));
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

    /// <param name="strict">Når true (manuelt skjema) kreves korrekt fødselsnr/orgnr-lengde.
    /// Når false (API/webhook) opprettes saken uansett — id = fødselsnr → mobil → fallback.</param>
    public async Task<(bool ok, string? error)> SaveAsync(Kundekort k, bool strict = false, string? aktor = null)
    {
        k.KundeId = (k.KundeId ?? "").Trim();
        BerikGeografi(k);   // fyll kommune/poststed/fylke fra postnummer når de mangler

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

        if (k.KundeType == "B2C" && string.IsNullOrWhiteSpace(k.Foedselsnummer) && k.KundeId.Length == 11)
            k.Foedselsnummer = k.KundeId;

        // B2B: hold orgnr-feltet i synk med et gyldig 9-sifret kunde_id.
        if (k.KundeType == "B2B" && string.IsNullOrWhiteSpace(k.Orgnr) && k.KundeId.Length == 9 && k.KundeId.All(char.IsDigit))
            k.Orgnr = k.KundeId;

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
                var resp = await _client.From<Kundekort>().Insert(ForDb(k));
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
                var resp = await _client.From<Kundekort>().Update(ForDb(k));
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
        "adresse,postnummer,poststed,kommune,fylke,laanetype,laaneformal," +
        "onsket_laanebelop,onsket_lopetid_mnd,aarsinntekt_brutto,sivilstatus," +
        "arbeidssituasjon,boforhold,naavaerende_rente,navarende_bank,boliggjeld," +
        "boligverdi,status,eier,eier_navn,eier_tatt_at,delegert_bank,kilde," +
        "siste_kontakt,neste_oppfolging,created_at,updated_at";

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
            if (k is not null) { k.Eier = eier; k.EierNavn = eierNavn; k.EierTattAt = naa; if (nyStatus is not null) k.Status = nyStatus; }
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

    public async Task SetStatusAsync(Guid id, string status, string? aktor = null)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.Status = status;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.Status, status).Update();
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
}
