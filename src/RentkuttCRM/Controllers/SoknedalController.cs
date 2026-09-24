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
///   POST /api/soknedal/signert   → «SBL Signert»
///   POST /api/soknedal/utbetalt  → «Utbetalt»
///   POST /api/soknedal/avsluttet → «Avsluttet»
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
    private readonly AlarmService _alarm;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SoknedalController> _log;

    public SoknedalController(SettingsService settings, KundekortService kundekort, LoggService logg,
        AlarmService alarm, IWebHostEnvironment env, ILogger<SoknedalController> log)
    {
        _settings = settings;
        _kundekort = kundekort;
        _logg = logg;
        _alarm = alarm;
        _env = env;
        _log = log;
    }

    [HttpPost("signert")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Signert() => Behandle(KundekortService.StatusSignert, "Soknedal: SBL signert.");

    [HttpPost("utbetalt")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Utbetalt() => Behandle(KundekortService.StatusUtbetalt, "Soknedal: godkjent og utbetalt.");

    [HttpPost("avsluttet")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Avsluttet() => Behandle(KundekortService.StatusAvsluttet, "Soknedal: sak avsluttet.");

    private async Task<IActionResult> Behandle(string nyStatus, string loggtekst)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        var expected = await _settings.GetAsync(KeyToken);
        if (string.IsNullOrWhiteSpace(expected))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Webhook-token er ikke satt opp." });
        var endepunkt = Request.Path.Value ?? "";
        if (!TokenMatch(expected, PresentedToken()))
        {
            _log.LogWarning("Soknedal-webhook avvist: ugyldig token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            await AlarmAsync($"Soknedal-webhook avvist ({endepunkt})",
                $"Ugyldig/manglende token fra IP {HttpContext.Connection.RemoteIpAddress}. Endepunkt: {endepunkt}.", "token");
            return Unauthorized(new { error = "Ugyldig eller manglende token." });
        }

        var (mobil, orgnr) = await LesIdentifikatorAsync();
        if (string.IsNullOrWhiteSpace(mobil) && string.IsNullOrWhiteSpace(orgnr))
        {
            _log.LogWarning("Soknedal-webhook 400: fant ingen identifikator. Content-Type={CT}", Request.ContentType ?? "(ingen)");
            await AlarmAsync($"Soknedal-webhook mangler identifikator ({endepunkt})",
                $"Innkommende webhook uten mobilnummer/orgnr. Content-Type: {Request.ContentType ?? "(ingen)"}.", "mangler-ident");
            return BadRequest(new { error = "Oppgi mobilnummer og/eller orgnr (som form-felt, query eller JSON)." });
        }

        var sak = await _kundekort.FinnForBankTilbakemeldingAsync(mobil, orgnr, BankNavn);
        if (sak is null)
        {
            _log.LogWarning("Soknedal-webhook 404: ingen match. mobil={Mobil} orgnr={Orgnr}", mobil, orgnr);
            await AlarmAsync($"Soknedal-webhook fant ingen sak ({endepunkt})",
                $"Soknedal sendte «{loggtekst}» men vi fant ingen matchende sak. Mobil: {mobil ?? "—"}, orgnr: {orgnr ?? "—"}. "
                + "Saken må finnes hos oss og enten være delegert til Soknedal eller udelegert.", $"ingen-match-{mobil}-{orgnr}");
            return NotFound(new { funnet = false, melding = "Fant ingen sak delegert til Soknedal som matcher mobilnummer/orgnr." });
        }

        await _kundekort.SetStatusAsync(sak.Id, nyStatus, "Soknedal (webhook)");
        // Var saken udelegert, registrer Soknedal som delegert bank nå (så senere webhooks treffer presist).
        if (string.IsNullOrWhiteSpace(sak.DelegertBank)) await _kundekort.SetDelegertBankAsync(sak.Id, BankNavn);
        await _logg.LoggAsync(sak.Id, "Soknedal (webhook)", loggtekst, "endring");
        _log.LogInformation("Soknedal-webhook: sak {Id} → {Status}", sak.Id, nyStatus);
        return Ok(new { funnet = true, id = sak.Id, ny_status = nyStatus });
    }

    // Reiser en alarm ved innkommende webhook-feil, så det er synlig i portalen hva som ble forsøkt.
    private async Task AlarmAsync(string tittel, string detalj, string noekkelDel)
    {
        try { await _alarm.RaiseAsync("Webhook", tittel, detalj, AlarmService.Alvorlighet.Advarsel, "Soknedal-webhook", $"soknedal-webhook-{noekkelDel}"); }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke reise Soknedal-webhook-alarm"); }
    }

    // Leser mobilnummer/orgnr fra query, form-body (Nextcom-stil) ELLER JSON-body — robust og
    // uavhengig av store/små bokstaver i feltnavnet (Soknedal kan sende «CellPhone», «MobileNumber» osv.).
    private async Task<(string? mobil, string? orgnr)> LesIdentifikatorAsync()
    {
        string? mobil = null, orgnr = null;
        static string N(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var mobilNavn = new[] { "mobilnummer", "mobil", "cellphone", "mobilephone", "mobilenumber", "phone", "phonenumber", "telefon", "tlf" }.Select(N).ToHashSet();
        // Merk: Soknedals innkommende webhook sender orgnr i feltet «ssn».
        var orgNavn = new[] { "orgnr", "organisasjonsnummer", "orgnummer", "orgno", "organizationnumber", "ssn" }.Select(N).ToHashSet();
        void Sett(string key, string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var k = N(key);
            if (mobil is null && mobilNavn.Contains(k)) mobil = val.Trim();
            else if (orgnr is null && orgNavn.Contains(k)) orgnr = val.Trim();
        }

        foreach (var kv in Request.Query) Sett(kv.Key, kv.Value.ToString());
        if (Request.HasFormContentType)
            foreach (var kv in Request.Form) Sett(kv.Key, kv.Value.ToString());

        // JSON-body ({"mobilnummer":"...","orgnr":"..."}) hvis vi ikke har begge ennå.
        if ((mobil is null || orgnr is null) && !Request.HasFormContentType)
        {
            try
            {
                Request.EnableBuffering();
                Request.Body.Position = 0;
                using var reader = new StreamReader(Request.Body, leaveOpen: true);
                var raw = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{"))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String) Sett(p.Name, p.Value.GetString());
                        else if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) Sett(p.Name, p.Value.GetRawText());
                    }
                }
            }
            catch { /* ikke JSON — ignorer */ }
        }
        return (mobil, orgnr);
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
