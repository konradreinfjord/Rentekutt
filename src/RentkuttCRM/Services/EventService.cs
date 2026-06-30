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

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
