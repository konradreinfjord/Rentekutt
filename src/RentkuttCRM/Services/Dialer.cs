using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>Ett utgående anrop startet via dialeren (Zisson click-to-call). Utfall/varighet
/// fylles inn i etterkant av <see cref="ZissonCdrWorker"/> fra Zisson CDR.</summary>
[Table("dialer_anrop")]
public class DialerAnrop : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("aktor")] public string? Aktor { get; set; }
    [Column("agent_guid")] public string? AgentGuid { get; set; }
    [Column("til_nummer")] public string? TilNummer { get; set; }
    [Column("zid")] public string? Zid { get; set; }
    [Column("startet_at", ignoreOnInsert: true)] public DateTime StartetAt { get; set; }
    [Column("status")] public string Status { get; set; } = DialerService.StatusUavklart;
    [Column("utfall")] public string? Utfall { get; set; }
    [Column("taletid_sek")] public int? TaletidSek { get; set; }
    [Column("ferdig_at")] public DateTime? FerdigAt { get; set; }
}

/// <summary>Lagring/oppslag av dialer-anrop. Feiler aldri hardt – ringing skal ikke velte på loggføring.</summary>
public class DialerService
{
    public const string StatusUavklart = "uavklart";
    public const string StatusFerdig = "ferdig";
    public const string StatusFeilet = "feilet";

    public const string UtfallSvart = "svart";
    public const string UtfallIkkeSvart = "ikke_svart";
    public const string UtfallUkjent = "ukjent";

    private readonly Supabase.Client _client;
    private readonly ILogger<DialerService> _log;
    public bool IsConfigured { get; }

    private static readonly List<DialerAnrop> _staging = new();
    private bool _initialized;

    public DialerService(Supabase.Client client, IConfiguration cfg, ILogger<DialerService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Registrerer et startet anrop. Returnerer raden (med Id) eller null ved feil.</summary>
    public async Task<DialerAnrop?> OpprettAsync(Guid kundekortId, string? aktor, string? agentGuid, string? tilNummer, string? zid)
    {
        var rad = new DialerAnrop
        {
            KundekortId = kundekortId,
            Aktor = aktor,
            AgentGuid = agentGuid,
            TilNummer = tilNummer,
            Zid = zid,
            Status = StatusUavklart,
        };
        if (!IsConfigured)
        {
            rad.Id = Guid.NewGuid(); rad.StartetAt = DateTime.UtcNow; _staging.Insert(0, rad); return rad;
        }
        try
        {
            await EnsureInitAsync();
            return (await _client.From<DialerAnrop>().Insert(rad)).Models.FirstOrDefault();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Lagring av dialer-anrop feilet"); return null; }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
