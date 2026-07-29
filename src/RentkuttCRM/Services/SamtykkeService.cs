using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("samtykke")]
public class Samtykke : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("formaal")] public string Formaal { get; set; } = "";
    [Column("gitt")] public bool Gitt { get; set; } = true;
    [Column("tekstversjon")] public string? Tekstversjon { get; set; }
    [Column("kilde")] public string? Kilde { get; set; }
    [Column("ip")] public string? Ip { get; set; }
    [Column("gitt_at")] public DateTime GittAt { get; set; }
    [Column("utlop")] public DateTime? Utlop { get; set; }
    [Column("created_at", ignoreOnInsert: true)] public DateTime CreatedAt { get; set; }
}

/// <summary>Admin-godkjenning av samtykke (fireøyne). To ulike admins → gyldig samtykke.</summary>
[Table("samtykke_godkjenning")]
public class SamtykkeGodkjenning : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("godkjent_av")] public string GodkjentAv { get; set; } = "";
    [Column("godkjent_navn")] public string? GodkjentNavn { get; set; }
    [Column("godkjent_at")] public DateTime GodkjentAt { get; set; }
}

/// <summary>Samtykke-håndtering (GDPR). Registrering, gyldighetssjekk og oppslag.</summary>
public class SamtykkeService
{
    /// <summary>Formål som kreves før kredittvurdering/oversendelse til bank.</summary>
    public const string FormaalKreditt = "Gjeldsregister og kredittsjekk";

    /// <summary>Versjon av samtykketeksten kunden godtar. Bump ved endring av selve teksten,
    /// så vi kan dokumentere nøyaktig hvilken ordlyd hvert samtykke er gitt mot.</summary>
    public const string SamtykketekstVersjon = "samtykke-v1";

    private readonly Supabase.Client _client;
    private readonly ILogger<SamtykkeService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Samtykke> _staging = new();
    private bool _initialized;

    public SamtykkeService(Supabase.Client client, IConfiguration cfg, ILogger<SamtykkeService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task RegistrerAsync(Guid kundekortId, string formaal, string? kilde, string? tekstversjon = null, string? ip = null, DateTime? utlop = null)
    {
        var s = new Samtykke
        {
            KundekortId = kundekortId, Formaal = formaal, Gitt = true,
            Tekstversjon = tekstversjon, Kilde = kilde, Ip = ip, GittAt = DateTime.UtcNow, Utlop = utlop,
        };
        if (!IsConfigured) { s.Id = Guid.NewGuid(); s.CreatedAt = DateTime.UtcNow; _staging.Insert(0, s); return; }
        try { await EnsureInitAsync(); await _client.From<Samtykke>().Insert(s); }
        catch (Exception ex) { _log.LogWarning(ex, "Registrering av samtykke feilet"); }
    }

    /// <summary>True hvis det finnes et gyldig (gitt, ikke utløpt) samtykke for formålet.</summary>
    public async Task<bool> HarGyldigAsync(Guid kundekortId, string formaal)
    {
        var naa = DateTime.UtcNow;
        if (!IsConfigured)
            return _staging.Any(x => x.KundekortId == kundekortId && x.Formaal == formaal && x.Gitt && (x.Utlop == null || x.Utlop > naa));
        try
        {
            await EnsureInitAsync();
            // NB: filtrer kun på kundekort_id i spørringen. Å kombinere flere betingelser
            // (særlig bool == true) i ett uttrykk gir en ugyldig PostgREST-logikktre i
            // klientversjonen vår — så vi evaluerer formål/gitt/utløp i minnet.
            var rader = (await _client.From<Samtykke>()
                .Where(x => x.KundekortId == kundekortId)
                .Get()).Models;
            return rader.Any(x => x.Formaal == formaal && x.Gitt && (x.Utlop == null || x.Utlop > naa));
        }
        catch (Exception ex) { _log.LogError(ex, "Sjekk av samtykke feilet"); return false; }
    }

    /// <summary>Gyldig samtykke der samtykke-ENTITETEN er fasit når den finnes (håndhever utløp),
    /// og det gamle boolske flagget kun brukes som fallback for eldre saker uten samtykke-rad.
    /// Retter FUNN A: tidligere kortsluttet det boolske flagget utløpssjekken.</summary>
    public async Task<bool> HarGyldigEllerLegacyAsync(Guid kundekortId, string formaal, bool legacyFlagg)
    {
        var naa = DateTime.UtcNow;
        List<Samtykke> rader;
        if (!IsConfigured)
        {
            rader = _staging.Where(x => x.KundekortId == kundekortId && x.Formaal == formaal).ToList();
        }
        else
        {
            try
            {
                await EnsureInitAsync();
                rader = (await _client.From<Samtykke>().Where(x => x.KundekortId == kundekortId).Get())
                    .Models.Where(x => x.Formaal == formaal).ToList();
            }
            catch (Exception ex) { _log.LogError(ex, "Sjekk av samtykke feilet"); return false; }
        }
        // Finnes samtykke-rad(er) for formålet → entiteten er fasit, inkl. utløp.
        if (rader.Count > 0) return rader.Any(x => x.Gitt && (x.Utlop == null || x.Utlop > naa));
        // Ingen rad (eldre sak) → fall tilbake på det boolske flagget.
        return legacyFlagg;
    }

    public async Task<List<Samtykke>> ForKundeAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _staging.Where(x => x.KundekortId == kundekortId).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Samtykke>()
                .Where(x => x.KundekortId == kundekortId)
                .Order(x => x.GittAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av samtykke feilet"); return new(); }
    }

    // ---- Admin-godkjenning (fireøyne): to ulike admins → gyldig samtykke ----
    private static readonly List<SamtykkeGodkjenning> _godkjStaging = new();

    /// <summary>Admin-godkjenninger av samtykke på et kundekort.</summary>
    public async Task<List<SamtykkeGodkjenning>> GodkjenningerAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _godkjStaging.Where(x => x.KundekortId == kundekortId).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<SamtykkeGodkjenning>().Where(x => x.KundekortId == kundekortId).Get()).Models;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av samtykke-godkjenninger feilet"); return new(); }
    }

    /// <summary>Registrer denne adminens godkjenning (fireøyne). Når TO ULIKE admins har godkjent,
    /// opprettes et gyldig samtykke (kilde «Admin dobbeltgodkjenning»). Returnerer distinkte
    /// godkjennere, om det nettopp ble aktivert, og full liste.</summary>
    public async Task<(int antall, bool aktivertNaa, List<SamtykkeGodkjenning> godkjenninger)> AdminGodkjennAsync(
        Guid kundekortId, string adminEpost, string? adminNavn)
    {
        adminEpost = (adminEpost ?? "").Trim();
        if (kundekortId == Guid.Empty || string.IsNullOrWhiteSpace(adminEpost))
            return (0, false, new());

        var eksisterende = await GodkjenningerAsync(kundekortId);
        var alleredeGodkjent = eksisterende.Any(g => string.Equals(g.GodkjentAv, adminEpost, StringComparison.OrdinalIgnoreCase));

        if (!alleredeGodkjent)
        {
            var ny = new SamtykkeGodkjenning { KundekortId = kundekortId, GodkjentAv = adminEpost, GodkjentNavn = adminNavn, GodkjentAt = DateTime.UtcNow };
            if (!IsConfigured) { ny.Id = Guid.NewGuid(); _godkjStaging.Insert(0, ny); }
            else { try { await EnsureInitAsync(); await _client.From<SamtykkeGodkjenning>().Insert(ny); } catch (Exception ex) { _log.LogError(ex, "Lagring av admin-godkjenning feilet"); } }
            eksisterende = await GodkjenningerAsync(kundekortId);
        }

        var distinkte = eksisterende.Select(g => g.GodkjentAv.Trim().ToLowerInvariant()).Distinct().Count();

        // To ulike admins → opprett gyldig samtykke (hvis ikke allerede gyldig).
        var aktivertNaa = false;
        if (distinkte >= 2 && !await HarGyldigAsync(kundekortId, FormaalKreditt))
        {
            await RegistrerAsync(kundekortId, FormaalKreditt, "Admin dobbeltgodkjenning", tekstversjon: SamtykketekstVersjon);
            aktivertNaa = true;
        }
        return (distinkte, aktivertNaa, eksisterende);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
