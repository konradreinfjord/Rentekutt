using System.Security.Cryptography;
using System.Text;

namespace RentkuttCRM.Services;

/// <summary>
/// Feltnivåkryptering av personopplysninger (fødselsnummer m.m.) med AES-256-GCM,
/// pluss en deterministisk, søkbar HMAC-nøkkel for likhets-oppslag på krypterte felt.
///
/// Nøkkel settes som app-innstilling <c>Gdpr__FieldKey</c> (base64 av 32 byte, eller
/// en vilkårlig passfrase som utledes til 32 byte). Uten nøkkel er tjenesten
/// DEAKTIVERT og alle operasjoner er gjennomslag (klartekst) — slik at appen kjører
/// videre uendret til nøkkelen settes, og eksisterende data kan bakfylles etterpå.
///
/// Lagringsformat: <c>enc:1:BASE64(nonce[12] | tag[16] | ciphertext)</c>.
/// Dekryptering slipper klartekst (legacy, uten prefiks) uendret gjennom, så en base
/// med blandet klartekst/kryptert lar seg lese under migrering.
/// </summary>
public class CryptoService
{
    private const string Prefiks = "enc:1:";
    private readonly byte[]? _encKey;
    private readonly byte[]? _macKey;

    /// <summary>True når en nøkkel er satt og kryptering/HMAC er aktiv.</summary>
    public bool IsEnabled => _encKey is not null;

    public CryptoService(IConfiguration cfg, ILogger<CryptoService> log)
    {
        var raw = cfg["Gdpr:FieldKey"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            log.LogWarning("Gdpr:FieldKey er ikke satt — feltnivåkryptering er AV (fødselsnummer lagres i klartekst).");
            return;
        }
        // Normaliser til nøyaktig 32 byte: bruk base64 direkte når den dekoder til 32 byte,
        // ellers utled 32 byte via SHA-256 av den oppgitte strengen (tåler passfraser).
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(raw.Trim());
            if (keyBytes.Length != 32) keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        }
        catch (FormatException) { keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw)); }

        _encKey = keyBytes;
        // Egen (avledet) nøkkel for HMAC, så krypterings- og søkenøkkel ikke er identiske.
        _macKey = SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToBase64String(keyBytes) + "|hmac-v1"));
        log.LogInformation("Feltnivåkryptering er PÅ (AES-256-GCM).");
    }

    public bool ErBeskyttet(string? verdi) => verdi is not null && verdi.StartsWith(Prefiks, StringComparison.Ordinal);

    /// <summary>Krypter en verdi. Idempotent (allerede kryptert → uendret). Klartekst når deaktivert eller tom.</summary>
    public string? Beskytt(string? klartekst)
    {
        if (string.IsNullOrEmpty(klartekst) || _encKey is null || ErBeskyttet(klartekst)) return klartekst;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(klartekst);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_encKey, 16);
        gcm.Encrypt(nonce, pt, ct, tag);
        var samlet = new byte[nonce.Length + tag.Length + ct.Length];
        Buffer.BlockCopy(nonce, 0, samlet, 0, 12);
        Buffer.BlockCopy(tag, 0, samlet, 12, 16);
        Buffer.BlockCopy(ct, 0, samlet, 28, ct.Length);
        return Prefiks + Convert.ToBase64String(samlet);
    }

    /// <summary>Dekrypter en verdi. Klartekst (legacy, uten prefiks) returneres uendret.</summary>
    public string? Avdekk(string? lagret)
    {
        if (string.IsNullOrEmpty(lagret) || !ErBeskyttet(lagret) || _encKey is null) return lagret;
        try
        {
            var samlet = Convert.FromBase64String(lagret[Prefiks.Length..]);
            var nonce = samlet[..12];
            var tag = samlet[12..28];
            var ct = samlet[28..];
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(_encKey, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch (CryptographicException) { return lagret; } // feil nøkkel/korrupt — ikke velt, returner rå verdi
        catch (FormatException) { return lagret; }
    }

    /// <summary>Deterministisk, søkbar HMAC (hex) av et fødselsnummer (kun sifre). Null hvis tomt/deaktivert.</summary>
    public string? HmacFnr(string? fnr)
    {
        if (_macKey is null) return null;
        var digits = new string((fnr ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;
        using var h = new HMACSHA256(_macKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(digits))).ToLowerInvariant();
    }
}
