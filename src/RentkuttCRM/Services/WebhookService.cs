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

    /// <summary>Validerer token i konstant tid og returnerer matchet inbound-webhook (eller null).</summary>
    public async Task<Webhook?> ValidateTokenAsync(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;

        IEnumerable<Webhook> hooks;
        if (!IsConfigured) { SeedStaging(); hooks = _staging; }
        else
        {
            try
            {
                await EnsureInitAsync();
                hooks = (await _client.From<Webhook>().Where(w => w.Direction == "inbound").Get()).Models;
            }
            catch (Exception ex) { _log.LogError(ex, "Token-validering feilet"); return null; }
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        Webhook? match = null;
        foreach (var h in hooks.Where(h => h.Active))
        {
            var stored = Encoding.UTF8.GetBytes(h.Token);
            if (stored.Length == presentedBytes.Length
                && CryptographicOperations.FixedTimeEquals(stored, presentedBytes))
                match = h; // ikke 'return' tidlig — unngå timing-lekkasje
        }
        return match;
    }

    /// <summary>Sjekker om en IP er tillatt for webhooken (tom liste = alle tillatt).</summary>
    public static bool IpAllowed(Webhook hook, string? ip)
    {
        if (string.IsNullOrWhiteSpace(hook.IpAllowlist)) return true;
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var allowed = hook.IpAllowlist.Split(new[] { ',', '\n', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        return allowed.Any(a => a.Trim() == ip.Trim());
    }

    public async Task SetIpAllowlistAsync(Guid id, string? list)
    {
        if (!IsConfigured)
        {
            var s = _staging.FirstOrDefault(w => w.Id == id);
            if (s is not null) s.IpAllowlist = list;
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Webhook>().Where(w => w.Id == id).Set(w => w.IpAllowlist!, list ?? "").Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke lagre IP-whitelist"); }
    }

    /// <summary>Registrerer sist mottatte data (uten PII) på webhooken.</summary>
    public async Task RecordReceiptAsync(Webhook hook, string info)
    {
        if (!IsConfigured)
        {
            var s = _staging.FirstOrDefault(w => w.Id == hook.Id);
            if (s is not null) { s.LastReceivedAt = DateTime.UtcNow; s.LastReceivedInfo = info; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Webhook>()
                .Where(w => w.Id == hook.Id)
                .Set(w => w.LastReceivedAt!, DateTime.UtcNow)
                .Set(w => w.LastReceivedInfo!, info)
                .Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke oppdatere sist mottatt"); }
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
