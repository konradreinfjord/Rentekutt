using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>Én bankpartner-pålogging: brukernavn/passord knyttet til én bank. API-et scoper alt til
/// denne banken — banken ser kun søknader som er sendt/delegert til seg.</summary>
public record PartnerKreds(string Bank, string Username, string Password);

/// <summary>
/// Lagrer og validerer bankpartner-pålogginger (JSON i innstillinger). Hver pålogging er knyttet til
/// en bank, slik at tokenet (og dermed API-tilgangen) begrenses til den bankens søknader.
/// </summary>
public class PartnerKredsService
{
    public const string Key = "partner_api_kreds";       // JSON-liste av PartnerKreds
    public const string KeySecret = "partner_api_secret"; // HMAC-nøkkel for token
    public const string KeyDocsKode = "partner_docs_kode"; // felles tilgangskode for /swagger-dokumentasjonen

    private readonly SettingsService _settings;
    public PartnerKredsService(SettingsService settings) => _settings = settings;

    /// <summary>Felles «symbolsk» tilgangskode for API-dokumentasjonen (/swagger). Ikke bankens
    /// brukernavn/passord — bare en enkel kode for å åpne dokumentasjonen.</summary>
    public Task<string?> DocsKodeAsync() => _settings.GetAsync(KeyDocsKode);
    public Task SettDocsKodeAsync(string? kode) => _settings.SetAsync(KeyDocsKode, (kode ?? "").Trim());

    public async Task<List<PartnerKreds>> HentAsync()
    {
        var raw = await _settings.GetAsync(Key);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<List<PartnerKreds>>(raw) ?? new(); }
        catch { return new(); }
    }

    public Task LagreAsync(List<PartnerKreds> liste) => _settings.SetAsync(Key, JsonSerializer.Serialize(liste));

    /// <summary>Henter (og oppretter ved behov) HMAC-nøkkelen som tokenene signeres med.</summary>
    public async Task<string> SecretAsync()
    {
        var sec = await _settings.GetAsync(KeySecret);
        if (string.IsNullOrWhiteSpace(sec))
        {
            sec = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await _settings.SetAsync(KeySecret, sec);
        }
        return sec;
    }

    /// <summary>Returnerer påloggingen som matcher brukernavn+passord (konstant tid), ellers null.</summary>
    public async Task<PartnerKreds?> ValiderAsync(string? brukernavn, string? passord)
    {
        if (string.IsNullOrWhiteSpace(brukernavn) || string.IsNullOrWhiteSpace(passord)) return null;
        PartnerKreds? treff = null;
        foreach (var k in await HentAsync())
            if (KonstantLik(k.Username, brukernavn) && KonstantLik(k.Password, passord))
                treff = k;   // ikke 'break' — unngå timing-lekkasje
        return treff;
    }

    public static bool KonstantLik(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
