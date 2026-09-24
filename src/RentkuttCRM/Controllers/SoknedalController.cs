using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Innkommende tilbakemeldings-webhooks fra Soknedal Sparebank. Soknedal POSTer til én av tre URL-er
/// for å oppdatere status på saken de har mottatt. Identifikator: mobilnummer og/eller orgnr — saken
/// må være delegert til Soknedal (settes automatisk ved sending).
///
/// Sikkerhet: HTTPS + token (Bearer eller ?token=, konstant-tids sammenligning). Token settes i
/// Admin → API og Data → Soknedal-kortet. Rate-limitet på IP.
///
/// URL-er:
///   POST /api/soknedal/pagaar    → «Sendt - I prosess» (under behandling)
///   POST /api/soknedal/signert   → «Signert» (SBL signert)
///   POST /api/soknedal/utbetalt  → «Utbetalt» (godkjent og utbetalt)
/// Felt (form eller query): mobilnummer, orgnr.
/// </summary>
[ApiController]
[Route("api/soknedal")]
public class SoknedalController : ControllerBase
{
    public const string KeyToken = "soknedal_webhook_token";
    public const string BankNavn = "Soknedal Sparebank";

    private readonly SettingsService _settings;
    private readonly KundekortService _kundekort;
    private readonly LoggService _logg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SoknedalController> _log;

    public SoknedalController(SettingsService settings, KundekortService kundekort, LoggService logg,
        IWebHostEnvironment env, ILogger<SoknedalController> log)
    {
        _settings = settings;
        _kundekort = kundekort;
        _logg = logg;
        _env = env;
        _log = log;
    }

    [HttpPost("pagaar")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Pagaar() => Behandle(KundekortService.StatusSendtIProsess, "Soknedal: søknad under behandling (pågår).");

    [HttpPost("signert")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Signert() => Behandle(KundekortService.StatusSignert, "Soknedal: SBL signert.");

    [HttpPost("utbetalt")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Utbetalt() => Behandle(KundekortService.StatusUtbetalt, "Soknedal: godkjent og utbetalt.");

    private async Task<IActionResult> Behandle(string nyStatus, string loggtekst)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        var expected = await _settings.GetAsync(KeyToken);
        if (string.IsNullOrWhiteSpace(expected))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Webhook-token er ikke satt opp." });
        if (!TokenMatch(expected, PresentedToken()))
        {
            _log.LogWarning("Soknedal-webhook avvist: ugyldig token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig eller manglende token." });
        }

        var (mobil, orgnr) = LesIdentifikator();
        if (string.IsNullOrWhiteSpace(mobil) && string.IsNullOrWhiteSpace(orgnr))
            return BadRequest(new { error = "Oppgi mobilnummer og/eller orgnr." });

        var sak = await _kundekort.FinnForBankTilbakemeldingAsync(mobil, orgnr, BankNavn);
        if (sak is null)
            return NotFound(new { funnet = false, melding = "Fant ingen sak delegert til Soknedal som matcher mobilnummer/orgnr." });

        await _kundekort.SetStatusAsync(sak.Id, nyStatus, "Soknedal (webhook)");
        await _logg.LoggAsync(sak.Id, "Soknedal (webhook)", loggtekst, "endring");
        _log.LogInformation("Soknedal-webhook: sak {Id} → {Status}", sak.Id, nyStatus);
        return Ok(new { funnet = true, id = sak.Id, ny_status = nyStatus });
    }

    // Leser mobilnummer/orgnr fra form-body (Nextcom-stil) eller query.
    private (string? mobil, string? orgnr) LesIdentifikator()
    {
        string? V(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (Request.HasFormContentType && Request.Form.TryGetValue(k, out var f) && !string.IsNullOrWhiteSpace(f)) return f.ToString().Trim();
                var q = Request.Query[k].ToString();
                if (!string.IsNullOrWhiteSpace(q)) return q.Trim();
            }
            return null;
        }
        return (V("mobilnummer", "mobil", "cellphone", "phone", "telefon"),
                V("orgnr", "organisasjonsnummer", "orgnummer", "orgno"));
    }

    private string? PresentedToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var q = Request.Query["token"].ToString();
        return string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    }

    private static bool TokenMatch(string expected, string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Genererer et nytt webhook-token (brukes fra Admin-kortet).</summary>
    public static string NewToken() => "swh_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
