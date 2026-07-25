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

/// <summary>Samtykke-håndtering (GDPR). Registrering, gyldighetssjekk og oppslag.</summary>
public class SamtykkeService
{
    /// <summary>Formål som kreves før kredittvurdering/oversendelse til bank.</summary>
    public const string FormaalKreditt = "Gjeldsregister og kredittsjekk";

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

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
