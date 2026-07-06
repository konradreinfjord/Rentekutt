using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Utgående GET-API for tredjeparter: slå opp en sak på mobilnummer og få forenklet
/// status tilbake (åpen / utbetalt / avslått). Ingen sensitive detaljer eksponeres.
/// Sikkerhet: HTTPS + Bearer-token (konstant-tids sammenligning). Token styres i Admin → API og Data → Tredjeparter.
/// </summary>
[ApiController]
[Route("api/tredjepart")]
public class TredjepartController : ControllerBase
{
    public const string KeyToken = "tredjepart_token";
    public const string KeyEnabled = "tredjepart_enabled";

    private readonly SettingsService _settings;
    private readonly KundekortService _kundekort;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TredjepartController> _log;

    public TredjepartController(SettingsService settings, KundekortService kundekort,
        IWebHostEnvironment env, ILogger<TredjepartController> log)
    {
        _settings = settings;
        _kundekort = kundekort;
        _env = env;
        _log = log;
    }

    /// <summary>GET /api/tredjepart/status?mobil=+4791234567 — returnerer status på nyeste sak for nummeret.</summary>
    [HttpGet("status")]
    [EnableRateLimiting("tredjepart")]
    public async Task<IActionResult> Status([FromQuery] string? mobil)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        if ((await _settings.GetAsync(KeyEnabled)) != "true")
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Tredjepart-API-et er ikke aktivert." });

        var expected = await _settings.GetAsync(KeyToken);
        if (string.IsNullOrWhiteSpace(expected) || !TokenMatch(expected, PresentedToken()))
        {
            _log.LogWarning("Tredjepart-API avvist: ugyldig token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig eller manglende token." });
        }

        if (string.IsNullOrWhiteSpace(mobil))
            return BadRequest(new { error = "Oppgi mobilnummer, f.eks. ?mobil=+4791234567" });

        var sak = await _kundekort.FindByMobilAsync(mobil);
        if (sak is null)
            return Ok(new { funnet = false, mobil, melding = "Ingen sak funnet på nummeret." });

        var (kode, tekst, ferdig) = KundekortService.TredjepartStatus(sak.Status);
        return Ok(new
        {
            funnet = true,
            mobil,
            status = kode,          // "apen" | "utbetalt" | "avslatt"
            status_tekst = tekst,
            ferdig,                 // true = ferdigbehandlet (utbetalt/avslått)
            opprettet = sak.CreatedAt,
            sist_oppdatert = sak.UpdatedAt,
        });
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

    /// <summary>Genererer et nytt API-token (brukes fra Admin-fanen).</summary>
    public static string NewToken() => "tpk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
