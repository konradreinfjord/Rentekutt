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

    public SettingsService(Supabase.Client client, IConfiguration cfg, ILogger<SettingsService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<string?> GetAsync(string key)
    {
        if (!IsConfigured) return _staging.GetValueOrDefault(key);
        try
        {
            await EnsureInitAsync();
            var row = await _client.From<Innstilling>().Where(x => x.Nokkel == key).Single();
            return row?.Verdi;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av innstilling {Key} feilet", key); return null; }
    }

    public async Task<int> GetIntAsync(string key, int fallback)
        => int.TryParse(await GetAsync(key), out var v) ? v : fallback;

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback)
    {
        var s = (await GetAsync(key))?.Replace(",", ".");
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public async Task SetAsync(string key, string? value)
    {
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
