using System.Security.Cryptography;
using System.Text;

namespace RentkuttCRM.Services;

/// <summary>
/// Inbound/outbound webhooks. Inbound mottar leads (finansielle data + NIN) som
/// mappes til kundekort. Tokens sammenlignes i konstant tid (mot timing-angrep).
/// </summary>
public class WebhookService
{
    public const string InboundName = "www.rentekutt.no";

    private readonly Supabase.Client _client;
    private readonly ILogger<WebhookService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Webhook> _staging = new();
    private static bool _seeded;
    private bool _initialized;

    public WebhookService(Supabase.Client client, IConfiguration cfg, ILogger<WebhookService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "whk_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task EnsureSeededPublicAsync()
    {
        if (!IsConfigured) { SeedStaging(); return; }
        if (_seeded) return;
        _seeded = true;
        try
        {
            await EnsureInitAsync();
            var existing = await _client.From<Webhook>()
                .Where(w => w.Direction == "inbound")
                .Where(w => w.Name == InboundName)
                .Get();
            if (existing.Models.Count == 0)
            {
                await _client.From<Webhook>().Insert(new Webhook
                {
                    Name = InboundName,
                    Direction = "inbound",
                    Token = NewToken(),
                    Active = true,
                });
                _log.LogInformation("Opprettet inbound webhook {Name}", InboundName);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Seeding av webhook feilet");
            _seeded = false;
        }
    }

    public async Task<Webhook?> GetInboundAsync()
    {
        if (!IsConfigured) { SeedStaging(); return _staging.FirstOrDefault(w => w.Direction == "inbound"); }
        try
        {
            await EnsureInitAsync();
            return await _client.From<Webhook>()
                .Where(w => w.Direction == "inbound")
                .Where(w => w.Name == InboundName)
                .Single();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av webhook feilet");
            return null;
        }
    }

    /// <summary>Validerer token i konstant tid mot aktive inbound-webhooks.</summary>
    public async Task<bool> ValidateTokenAsync(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;

        IEnumerable<Webhook> hooks;
        if (!IsConfigured) { SeedStaging(); hooks = _staging; }
        else
        {
            try
            {
                await EnsureInitAsync();
                hooks = (await _client.From<Webhook>().Where(w => w.Direction == "inbound").Get()).Models;
            }
            catch (Exception ex) { _log.LogError(ex, "Token-validering feilet"); return false; }
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var ok = false;
        foreach (var h in hooks.Where(h => h.Active))
        {
            var stored = Encoding.UTF8.GetBytes(h.Token);
            if (stored.Length == presentedBytes.Length
                && CryptographicOperations.FixedTimeEquals(stored, presentedBytes))
                ok = true; // ikke 'return' tidlig — unngå timing-lekkasje
        }
        return ok;
    }

    private void SeedStaging()
    {
        if (_staging.Count > 0) return;
        _staging.Add(new Webhook { Id = Guid.NewGuid(), Name = InboundName, Direction = "inbound", Token = NewToken(), Active = true });
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
