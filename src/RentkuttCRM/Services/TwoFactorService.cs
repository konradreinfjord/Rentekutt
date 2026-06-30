using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RentkuttCRM.Services;

/// <summary>
/// 2FA-koder ved innlogging. Genererer 6-sifret kode, sender via SMS (LinkMobility),
/// og verifiserer. Koder holdes i minnet med 5 min levetid (engangsbruk).
/// Rategrense per bruker: maks N SMS/time og M SMS/24t (konfigurerbart i Admin).
/// </summary>
public class TwoFactorService
{
    public const string KeyMaxPerHour = "sms_max_per_time";
    public const string KeyMaxPer24h = "sms_max_per_24h";
    public const int DefaultMaxPerHour = 3;
    public const int DefaultMaxPer24h = 5;

    private static readonly TimeSpan Levetid = TimeSpan.FromMinutes(5);

    private readonly LinkMobilityService _sms;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TwoFactorService> _log;
    private readonly ConcurrentDictionary<string, (string Code, DateTime Expires)> _codes = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _sendLog = new();

    public TwoFactorService(LinkMobilityService sms, IServiceScopeFactory scopeFactory, ILogger<TwoFactorService> log)
    {
        _sms = sms;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool KanSende(UserRow user) =>
        _sms.ErKonfigurert && !string.IsNullOrWhiteSpace(user.Mobilnummer);

    public async Task<(bool ok, string? error)> SendKodeAsync(UserRow user)
    {
        if (string.IsNullOrWhiteSpace(user.Mobilnummer))
            return (false, "Brukeren mangler mobilnummer.");

        var (maxPerHour, maxPer24h) = await HentGrenserAsync();
        var key = user.Email.ToLowerInvariant();
        var now = DateTime.UtcNow;

        // Rategrense per bruker (reserver plassen optimistisk).
        var log = _sendLog.GetOrAdd(key, _ => new List<DateTime>());
        lock (log)
        {
            log.RemoveAll(t => t < now.AddHours(-24));
            if (log.Count(t => t >= now.AddHours(-1)) >= maxPerHour)
                return (false, $"For mange koder sendt ({maxPerHour}/time). Vent litt og prøv igjen.");
            if (log.Count >= maxPer24h)
                return (false, $"For mange koder sendt ({maxPer24h}/24t). Prøv igjen senere.");
            log.Add(now);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codes[key] = (code, now.Add(Levetid));

        var (ok, _, detalj) = await _sms.SendSmsAsync(
            user.Mobilnummer, $"Din Rentekutt-innloggingskode: {code} (gyldig i 5 minutter).");
        if (!ok)
        {
            // Rull tilbake reservasjonen ved sendefeil.
            lock (log) { log.Remove(now); }
            _log.LogWarning("2FA SMS feilet: {Detalj}", detalj);
            return (false, "Kunne ikke sende SMS-kode. Prøv igjen eller kontakt admin.");
        }
        return (true, null);
    }

    public bool Verifiser(string email, string? kode)
    {
        email = (email ?? "").ToLowerInvariant();
        if (!_codes.TryGetValue(email, out var c)) return false;
        if (DateTime.UtcNow > c.Expires) { _codes.TryRemove(email, out _); return false; }

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(c.Code), Encoding.UTF8.GetBytes((kode ?? "").Trim()));
        if (ok) _codes.TryRemove(email, out _);
        return ok;
    }

    private async Task<(int perHour, int per24h)> HentGrenserAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var perHour = await settings.GetIntAsync(KeyMaxPerHour, DefaultMaxPerHour);
            var per24h = await settings.GetIntAsync(KeyMaxPer24h, DefaultMaxPer24h);
            return (perHour, per24h);
        }
        catch
        {
            return (DefaultMaxPerHour, DefaultMaxPer24h);
        }
    }
}
