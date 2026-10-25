using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("hendelser")]
public class Hendelse : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("tidspunkt", ignoreOnInsert: true)] public DateTime Tidspunkt { get; set; }
    [Column("type")] public string Type { get; set; } = "";
    [Column("beskrivelse")] public string? Beskrivelse { get; set; }
    [Column("kilde")] public string? Kilde { get; set; }
}

/// <summary>Systemhendelser (ingen PII). Brukes av Hendelseslogg-fanen.</summary>
public class EventService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<EventService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Hendelse> _staging = new();
    private bool _initialized;

    public EventService(Supabase.Client client, IConfiguration cfg, ILogger<EventService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task LogAsync(string type, string? beskrivelse, string? kilde)
    {
        if (!IsConfigured)
        {
            _staging.Insert(0, new Hendelse { Id = Guid.NewGuid(), Tidspunkt = DateTime.UtcNow, Type = type, Beskrivelse = beskrivelse, Kilde = kilde });
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Hendelse>().Insert(new Hendelse { Type = type, Beskrivelse = beskrivelse, Kilde = kilde });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Logging av hendelse feilet"); }
    }

    public async Task<List<Hendelse>> ListAsync(int limit = 100)
    {
        if (!IsConfigured) return _staging.Take(limit).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Hendelse>()
                .Order(x => x.Tidspunkt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(limit).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av hendelser feilet"); return new(); }
    }

    /// <summary>Alle hendelser av gitte typer etter et tidspunkt (paginert) — f.eks. «Innlogging»/«Utlogging»
    /// til arbeidstidsberegning i rapport-API-et.</summary>
    public async Task<List<Hendelse>> TyperSidenAsync(IEnumerable<string> typer, DateTime fraUtc)
    {
        var t = typer.ToList();
        if (!IsConfigured) return _staging.Where(h => t.Contains(h.Type) && h.Tidspunkt >= fraUtc).ToList();
        var res = new List<Hendelse>();
        try
        {
            await EnsureInitAsync();
            const int side = 1000;
            for (var from = 0; from <= 100000; from += side)
            {
                var batch = (await _client.From<Hendelse>()
                    .Filter("type", Constants.Operator.In, t)
                    .Filter("tidspunkt", Constants.Operator.GreaterThanOrEqual, fraUtc.ToUniversalTime().ToString("o"))
                    .Order(x => x.Tidspunkt, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                    .Range(from, from + side - 1)
                    .Get()).Models;
                res.AddRange(batch);
                if (batch.Count < side) break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av hendelser ({Typer}) feilet", string.Join(",", t)); }
        return res;
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
