using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RentkuttCRM.Services;

/// <summary>
/// 2FA-koder ved innlogging. Genererer 6-sifret kode, sender via SMS (LinkMobility),
/// og verifiserer. Koder holdes i minnet med 5 min levetid (engangsbruk).
/// </summary>
public class TwoFactorService
{
    private static readonly TimeSpan Levetid = TimeSpan.FromMinutes(5);

    private readonly LinkMobilityService _sms;
    private readonly ILogger<TwoFactorService> _log;
    private readonly ConcurrentDictionary<string, (string Code, DateTime Expires)> _codes = new();

    public TwoFactorService(LinkMobilityService sms, ILogger<TwoFactorService> log)
    {
        _sms = sms;
        _log = log;
    }

    /// <summary>Kan 2FA håndheves for brukeren? (SMS konfigurert + mobilnummer finnes)</summary>
    public bool KanSende(UserRow user) =>
        _sms.ErKonfigurert && !string.IsNullOrWhiteSpace(user.Mobilnummer);

    public async Task<(bool ok, string? error)> SendKodeAsync(UserRow user)
    {
        if (string.IsNullOrWhiteSpace(user.Mobilnummer))
            return (false, "Brukeren mangler mobilnummer.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codes[user.Email.ToLowerInvariant()] = (code, DateTime.UtcNow.Add(Levetid));

        var (ok, _, detalj) = await _sms.SendSmsAsync(
            user.Mobilnummer, $"Din Rentekutt-innloggingskode: {code} (gyldig i 5 minutter).");
        if (!ok)
        {
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
}
