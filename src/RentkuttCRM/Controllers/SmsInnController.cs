using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Inbound SMS (MO — mobile originated): tar imot kundesvar fra LinkMobility sin MO-webhook og
/// kobler dem til riktig kundekort. Registrer URL-en hos LinkMobility Support med ?token=…-parameteren.
/// Fleksibel feltgjenkjenning (JSON eller skjema-encoded) så den tåler LinkMobility sitt MO-format.
/// </summary>
[ApiController]
[Route("api/sms")]
public class SmsInnController : ControllerBase
{
    public const string KeyToken = "sms_inn_token";

    private readonly InnkommendeSmsService _inn;
    private readonly SettingsService _settings;
    private readonly ILogger<SmsInnController> _log;

    public SmsInnController(InnkommendeSmsService inn, SettingsService settings, ILogger<SmsInnController> log)
    {
        _inn = inn;
        _settings = settings;
        _log = log;
    }

    [HttpPost("inn")]
    [EnableRateLimiting("webhook")]
    public async Task<IActionResult> Inn([FromQuery] string? token)
    {
        // Token: query-param (?token=) eller header X-Sms-Token. Konstant-tids sammenligning.
        var forventet = await _settings.GetAsync(KeyToken);
        var presentert = !string.IsNullOrWhiteSpace(token) ? token : Request.Headers["X-Sms-Token"].ToString();
        if (string.IsNullOrWhiteSpace(forventet))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Inbound SMS er ikke aktivert (mangler token)." });
        if (!KonstantLik(forventet, presentert))
            return Unauthorized(new { error = "Ugyldig token." });

        // Les rå body (JSON eller skjema) fleksibelt.
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync();

        var felt = ParseFelt(raw, Request);
        var fra = Foerste(felt, "source", "sender", "from", "msisdn", "originator", "sourceaddr", "sourcenumber", "sourceaddress");
        var til = Foerste(felt, "destination", "receiver", "to", "shortnumber", "destinationaddr", "destinationaddress");
        var tekst = Foerste(felt, "text", "message", "content", "body", "messagetext", "sms", "smstext");

        if (string.IsNullOrWhiteSpace(fra))
        {
            _log.LogWarning("Innkommende SMS: fant ikke avsendernummer i payload. Rå: {Raw}", Kort(raw));
            return BadRequest(new { error = "Mangler avsendernummer (source/sender/from/msisdn)." });
        }

        var rad = await _inn.MottaAsync(fra!, til, tekst ?? "", Kort(raw, 4000));
        _log.LogInformation("Innkommende SMS mottatt fra {Fra} (sak: {Sak}).", fra, rad.KundekortId?.ToString() ?? "ingen match");
        return Ok(new { ok = true, matchetSak = rad.KundekortId });
    }

    // ---- Hjelpere ----

    private static bool KonstantLik(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string? Foerste(Dictionary<string, string> felt, params string[] navn)
    {
        foreach (var n in navn)
            if (felt.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    // Flater ut payload til små-bokstavs feltnavn → verdi. Støtter JSON (også nøstet) og skjema/query.
    private static Dictionary<string, string> ParseFelt(string raw, HttpRequest req)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var trimmet = raw.TrimStart();
            if (trimmet.StartsWith("{") || trimmet.StartsWith("["))
            {
                try { using var doc = JsonDocument.Parse(raw); Flat(doc.RootElement, d); }
                catch { /* ikke JSON — prøv skjema under */ }
            }
            if (d.Count == 0 && raw.Contains('='))
            {
                foreach (var par in raw.Split('&'))
                {
                    var i = par.IndexOf('=');
                    if (i <= 0) continue;
                    var k = Uri.UnescapeDataString(par[..i]).Trim();
                    var v = Uri.UnescapeDataString(par[(i + 1)..].Replace('+', ' '));
                    if (!d.ContainsKey(k)) d[k] = v;
                }
            }
        }

        // Query-parametre som fallback (noen MO-oppsett sender alt i query-strengen).
        foreach (var q in req.Query)
            if (!d.ContainsKey(q.Key)) d[q.Key] = q.Value.ToString();

        return d;
    }

    private static void Flat(JsonElement el, Dictionary<string, string> d)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Flat(p.Value, d);
                    else if (!d.ContainsKey(p.Name)) d[p.Name] = p.Value.ToString();
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Flat(item, d);
                break;
        }
    }

    private static string Kort(string? s, int maks = 500)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > maks ? s[..maks] : s);
}
