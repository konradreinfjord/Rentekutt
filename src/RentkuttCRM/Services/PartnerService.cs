using Newtonsoft.Json;
using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("partnere")]
public class Partner : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("navn")] public string Navn { get; set; } = "";
    [Column("provisjon")] public string? Provisjon { get; set; }
    [Column("engangssum")] public string? Engangssum { get; set; }

    // Transient (ikke persistert) — metode-valg i API-fanen. JsonIgnore så den
    // ikke sendes til databasen (ingen Method-kolonne der).
    [JsonIgnore] public string Method { get; set; } = "Webhook";
}

/// <summary>Bankpartnere lagret i databasen (med staging-fallback).</summary>
public class PartnerService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<PartnerService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Partner> _staging = new();
    private bool _initialized;

    public PartnerService(Supabase.Client client, IConfiguration cfg, ILogger<PartnerService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<Partner>> ListAsync()
    {
        if (!IsConfigured) return _staging.ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Partner>().Order(x => x.Navn, Constants.Ordering.Ascending, Constants.NullPosition.Last).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av partnere feilet"); return new(); }
    }

    public async Task<(bool ok, string? error)> AddAsync(string navn, string? provisjon, string? engangssum)
    {
        navn = (navn ?? "").Trim();
        if (string.IsNullOrWhiteSpace(navn)) return (false, "Banknavn er påkrevd.");
        var p = new Partner { Navn = navn, Provisjon = provisjon, Engangssum = engangssum };
        if (!IsConfigured)
        {
            if (_staging.Any(x => x.Navn.Equals(navn, StringComparison.OrdinalIgnoreCase))) return (false, "Partneren finnes allerede.");
            p.Id = Guid.NewGuid();
            _staging.Add(p);
            return (true, null);
        }
        try
        {
            await EnsureInitAsync();
            var finnes = await _client.From<Partner>().Where(x => x.Navn == navn).Get();
            if (finnes.Models.Count > 0) return (false, "Partneren finnes allerede.");
            await _client.From<Partner>().Insert(p);
            return (true, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av partner feilet"); return (false, "Teknisk feil ved lagring."); }
    }

    public async Task UpdateCompAsync(Guid id, string? provisjon, string? engangssum)
    {
        if (!IsConfigured)
        {
            var p = _staging.FirstOrDefault(x => x.Id == id);
            if (p is not null) { p.Provisjon = provisjon; p.Engangssum = engangssum; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Partner>().Where(x => x.Id == id)
                .Set(x => x.Provisjon!, provisjon ?? "")
                .Set(x => x.Engangssum!, engangssum ?? "")
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av kompensasjon feilet"); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
