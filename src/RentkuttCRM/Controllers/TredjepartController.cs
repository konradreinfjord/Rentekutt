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

    /// <summary>
    /// GET /api/tredjepart/rapport/openapi.json — OpenAPI 3.1-skjema for rapport-API-et, til «Import from URL»
    /// i ChatGPT Actions / Custom GPT, Power BI, Make m.fl. Åpent (inneholder ingen data, kun beskrivelse);
    /// server-URL-en settes fra faktisk vertsnavn så den aldri kan skrives feil.
    /// </summary>
    [HttpGet("rapport/openapi.json")]
    [EnableRateLimiting("tredjepart")]
    public IActionResult RapportOpenApi()
    {
        var server = $"{(Request.IsHttps || !_env.IsDevelopment() ? "https" : "http")}://{Request.Host}";
        var doc = new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "Rentekutt Rapport-API",
                version = "1.0.0",
                description = "Aggregert arbeidsrapport (antall, summer, snitt). Inneholder ingen kundedata. Alle parametre er valgfrie: standard er siste 30 dager, alle agenter og begge kundetyper.",
            },
            servers = new[] { new { url = server } },
            paths = new Dictionary<string, object>
            {
                ["/api/tredjepart/rapport"] = new
                {
                    get = new
                    {
                        operationId = "hentRapport",
                        summary = "Hent arbeidsrapport for en periode",
                        description = "Returnerer øyeblikksbilde (naa), innkommet, agentaktivitet, bank og saksutfall for perioden fra–til (Oslo-datoer, inkl.).",
                        parameters = new object[]
                        {
                            new { name = "fra", @in = "query", required = false, description = "Første dag i perioden, yyyy-MM-dd. Standard: 29 dager før «til».", schema = new { type = "string", format = "date" } },
                            new { name = "til", @in = "query", required = false, description = "Siste dag i perioden, yyyy-MM-dd. Standard: i dag.", schema = new { type = "string", format = "date" } },
                            new { name = "agent", @in = "query", required = false, description = "Navn eller e-post på én agent. Utelat for alle agenter (gir også tabell per agent).", schema = new { type = "string" } },
                            new { name = "kundetype", @in = "query", required = false, description = "B2C eller B2B. Utelat for begge.", schema = new { type = "string", @enum = new[] { "B2C", "B2B" } } },
                        },
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "Rapport (JSON) med seksjonene periode, filter, naa, innkommet, agentaktivitet, bank og saksutfall.", content = new Dictionary<string, object> { ["application/json"] = new { schema = new { type = "object" } } } },
                            ["400"] = new { description = "Ugyldig dato, kundetype eller for lang periode (maks 2 år)." },
                            ["401"] = new { description = "Ugyldig eller manglende token." },
                            ["404"] = new { description = "Ukjent agent — svaret inneholder listen over gyldige agentnavn." },
                            ["503"] = new { description = "Rapport-API-et er ikke aktivert." },
                        },
                        security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = Array.Empty<string>() } },
                    },
                },
            },
            components = new
            {
                securitySchemes = new Dictionary<string, object>
                {
                    ["bearerAuth"] = new { type = "http", scheme = "bearer", description = "API-token fra Admin → Tredjeparter → Rapport-API." },
                },
            },
        };
        return new JsonResult(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
