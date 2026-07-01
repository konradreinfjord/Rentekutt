using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("innstillinger")]
public class Innstilling : BaseModel
{
    [PrimaryKey("nokkel", true)] public string Nokkel { get; set; } = "";
    [Column("verdi")] public string? Verdi { get; set; }
}

/// <summary>Enkel key/value-innstillinger i databasen (med staging-fallback).</summary>
public class SettingsService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<SettingsService> _log;
    public bool IsConfigured { get; }

    private static readonly Dictionary<string, string?> _staging = new();
    private bool _initialized;
    // Alle innstillinger hentes i én spørring og caches for økten (unngår N round-trips).
    private Dictionary<string, string?>? _cache;

    public SettingsService(Supabase.Client client, IConfiguration cfg, ILogger<SettingsService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    private async Task<Dictionary<string, string?>> CacheAsync()
    {
        if (_cache is not null) return _cache;
        if (!IsConfigured) return _cache = new(_staging);
        try
        {
            await EnsureInitAsync();
            var rows = (await _client.From<Innstilling>().Get()).Models;
            _cache = rows.ToDictionary(r => r.Nokkel, r => r.Verdi);
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av innstillinger feilet"); _cache = new(); }
        return _cache;
    }

    public async Task<string?> GetAsync(string key) => (await CacheAsync()).GetValueOrDefault(key);

    public async Task<int> GetIntAsync(string key, int fallback)
        => int.TryParse(await GetAsync(key), out var v) ? v : fallback;

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback)
    {
        var s = (await GetAsync(key))?.Replace(",", ".");
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public async Task SetAsync(string key, string? value)
    {
        if (_cache is not null) _cache[key] = value;   // hold cache i synk
        if (!IsConfigured) { _staging[key] = value; return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<Innstilling>().Upsert(new Innstilling { Nokkel = key, Verdi = value });
        }
        catch (Exception ex) { _log.LogError(ex, "Lagring av innstilling {Key} feilet", key); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
