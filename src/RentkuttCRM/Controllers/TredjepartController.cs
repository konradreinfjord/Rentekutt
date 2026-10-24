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
    // Rapport-API-et (arbeidsdata til ledelsen) har egen av/på og eget token — annen målgruppe enn status-API-et.
    public const string KeyRapportToken = "rapport_token";
    public const string KeyRapportEnabled = "rapport_enabled";

    private readonly SettingsService _settings;
    private readonly KundekortService _kundekort;
    private readonly RapportService _rapport;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TredjepartController> _log;

    public TredjepartController(SettingsService settings, KundekortService kundekort, RapportService rapport,
        IWebHostEnvironment env, ILogger<TredjepartController> log)
    {
        _settings = settings;
        _kundekort = kundekort;
        _rapport = rapport;
        _env = env;
        _log = log;
    }

    /// <summary>
    /// GET /api/tredjepart/rapport?fra=2026-10-01&amp;til=2026-10-31&amp;agent=Navn&amp;kundetype=B2B
    /// Aggregert arbeidsrapport (antall/summer/snitt) — ingen kundedata. Alle parametre er valgfrie:
    /// standard er siste 30 dager, alle agenter og begge kundetyper.
    /// </summary>
    [HttpGet("rapport")]
    [EnableRateLimiting("tredjepart")]
    public async Task<IActionResult> Rapport([FromQuery] string? fra, [FromQuery] string? til,
        [FromQuery] string? agent, [FromQuery] string? kundetype)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        if ((await _settings.GetAsync(KeyRapportEnabled)) != "true")
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Rapport-API-et er ikke aktivert." });

        var expected = await _settings.GetAsync(KeyRapportToken);
        if (string.IsNullOrWhiteSpace(expected) || !TokenMatch(expected, PresentedToken()))
        {
            _log.LogWarning("Rapport-API avvist: ugyldig token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig eller manglende token." });
        }

        // Datoer: yyyy-MM-dd (Oslo), fra og med / til og med. Standard: siste 30 dager. Maks 2 år.
        var iDag = DateOnly.FromDateTime(Tid.IDag);
        DateOnly tilD = iDag, fraD = iDag.AddDays(-29);
        if (!string.IsNullOrWhiteSpace(til) && !DateOnly.TryParseExact(til.Trim(), "yyyy-MM-dd", out tilD))
            return BadRequest(new { error = "Ugyldig «til». Bruk formatet yyyy-MM-dd, f.eks. til=2026-10-31" });
        if (!string.IsNullOrWhiteSpace(fra) && !DateOnly.TryParseExact(fra.Trim(), "yyyy-MM-dd", out fraD))
            return BadRequest(new { error = "Ugyldig «fra». Bruk formatet yyyy-MM-dd, f.eks. fra=2026-10-01" });
        if (string.IsNullOrWhiteSpace(fra) && !string.IsNullOrWhiteSpace(til)) fraD = tilD.AddDays(-29);
        if (fraD > tilD) (fraD, tilD) = (tilD, fraD);
        if (tilD.DayNumber - fraD.DayNumber > 731)
            return BadRequest(new { error = "Perioden kan maks være 2 år." });

        if (!string.IsNullOrWhiteSpace(kundetype) && !RapportService.Kundetyper.Contains(kundetype.Trim(), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = "Ugyldig «kundetype». Gyldige verdier: B2C, B2B (utelat for begge)." });

        UserRow? agentRad = null;
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var agenter = await _rapport.AgenterAsync();
            agentRad = RapportService.FinnAgent(agenter, agent);
            if (agentRad is null)
                return NotFound(new { error = $"Fant ingen agent som matcher «{agent.Trim()}».", agenter = agenter.Select(a => a.FullName).ToArray() });
        }

        return Ok(await _rapport.LagAsync(fraD, tilD, agentRad, kundetype));
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
