using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>Én bankpartner-pålogging: brukernavn + passord-hash knyttet til én bank. API-et scoper
/// alt til denne banken — banken ser kun søknader som er sendt/delegert til seg.
/// Passordet lagres KUN som PBKDF2-hash (aldri i klartekst) og vises bare én gang ved opprettelse.</summary>
public class PartnerKreds
{
    public string Bank { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Format: pbkdf2$&lt;iterasjoner&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>Eldre klartekst-passord (kun for migrering — hashes og nulles ved første lesing).</summary>
    public string? Password { get; set; }
    public DateTime? Opprettet { get; set; }
}

/// <summary>
/// Lagrer og validerer bankpartner-pålogginger (JSON i innstillinger). Hver pålogging er knyttet til
/// en bank, slik at tokenet (og dermed API-tilgangen) begrenses til den bankens søknader.
/// </summary>
public class PartnerKredsService
{
    public const string Key = "partner_api_kreds";        // JSON-liste av PartnerKreds
    public const string KeySecret = "partner_api_secret";  // HMAC-nøkkel for token
    public const string KeyDocsKode = "partner_docs_kode"; // felles tilgangskode for /swagger-dokumentasjonen
    private const int Pbkdf2Iterasjoner = 210_000;        // OWASP-anbefaling for PBKDF2-SHA256

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
        List<PartnerKreds> liste;
        try { liste = JsonSerializer.Deserialize<List<PartnerKreds>>(raw) ?? new(); }
        catch { return new(); }

        // Migrering: eldre rader med klartekst-passord hashes og klarteksten slettes.
        var migrert = false;
        foreach (var k in liste.Where(k => string.IsNullOrEmpty(k.PasswordHash) && !string.IsNullOrEmpty(k.Password)))
        {
            k.PasswordHash = Hash(k.Password!);
            k.Password = null;
            migrert = true;
        }
        if (migrert) await LagreAsync(liste);
        return liste;
    }

    public Task LagreAsync(List<PartnerKreds> liste) => _settings.SetAsync(Key, JsonSerializer.Serialize(liste));

    /// <summary>Legg til en pålogging. Passordet hashes før lagring — klarteksten returneres ikke og
    /// må vises til brukeren umiddelbart.</summary>
    public async Task LeggTilAsync(string bank, string brukernavn, string passord)
    {
        var liste = await HentAsync();
        liste.Add(new PartnerKreds
        {
            Bank = bank.Trim(),
            Username = brukernavn.Trim(),
            PasswordHash = Hash(passord),
            Opprettet = DateTime.UtcNow,
        });
        await LagreAsync(liste);
        await SecretAsync();   // sørg for at token-nøkkel finnes
    }

    public async Task FjernAsync(string brukernavn)
    {
        var liste = await HentAsync();
        liste.RemoveAll(k => string.Equals(k.Username, brukernavn, StringComparison.OrdinalIgnoreCase));
        await LagreAsync(liste);
    }

    /// <summary>Henter (og oppretter ved behov) HMAC-nøkkelen som tokenene signeres med.</summary>
    public async Task<string> SecretAsync()
    {
        var sec = await _settings.GetAsync(KeySecret);
        if (string.IsNullOrWhiteSpace(sec))
        {
            sec = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
            await _settings.SetAsync(KeySecret, sec);
        }
        return sec;
    }

    /// <summary>Returnerer påloggingen som matcher brukernavn+passord, ellers null.
    /// Verifiserer alltid alle rader (konstant arbeid) for å unngå timing-lekkasje.</summary>
    public async Task<PartnerKreds?> ValiderAsync(string? brukernavn, string? passord)
    {
        if (string.IsNullOrWhiteSpace(brukernavn) || string.IsNullOrWhiteSpace(passord)) return null;
        PartnerKreds? treff = null;
        foreach (var k in await HentAsync())
        {
            var brukerOk = KonstantLik(k.Username, brukernavn);
            var passOk = !string.IsNullOrEmpty(k.PasswordHash) && Verifiser(passord, k.PasswordHash);
            if (brukerOk && passOk) treff = k;
        }
        return treff;
    }

    // ---- PBKDF2-SHA256 ----

    public static string Hash(string passord)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(passord, salt, Pbkdf2Iterasjoner, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Pbkdf2Iterasjoner}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verifiser(string passord, string lagret)
    {
        try
        {
            var d = lagret.Split('$');
            if (d.Length != 4 || d[0] != "pbkdf2") return false;
            var iter = int.Parse(d[1]);
            var salt = Convert.FromBase64String(d[2]);
            var forventet = Convert.FromBase64String(d[3]);
            var hash = Rfc2898DeriveBytes.Pbkdf2(passord, salt, iter, HashAlgorithmName.SHA256, forventet.Length);
            return CryptographicOperations.FixedTimeEquals(hash, forventet);
        }
        catch { return false; }
    }

    public static bool KonstantLik(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
