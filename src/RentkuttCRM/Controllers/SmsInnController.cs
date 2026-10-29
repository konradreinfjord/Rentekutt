using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
    private readonly SmsInnDebugLogg _debug;
    private readonly EventService _events;
    private readonly ILogger<SmsInnController> _log;

    public SmsInnController(InnkommendeSmsService inn, SettingsService settings, SmsInnDebugLogg debug, EventService events, ILogger<SmsInnController> log)
    {
        _inn = inn;
        _settings = settings;
        _debug = debug;
        _events = events;
        _log = log;
    }

    // Tåler alle vanlige gateway-oppsett: POST (JSON/XML/skjema) eller GET med query-felt, og token
    // i query (?token=), i stien (/inn/{token}), i header (X-Sms-Token) eller som skjemafelt.
    [HttpPost("inn")]
    [HttpGet("inn")]
    [HttpPost("inn/{tokenSti}")]
    [HttpGet("inn/{tokenSti}")]
    [EnableRateLimiting("webhook")]
    public async Task<IActionResult> Inn([FromQuery] string? token, string? tokenSti = null)
    {
        // Les rå body (JSON/XML/skjema) først — så vi alltid kan logge hva som faktisk kom inn.
        // Skjema-kodet body (application/x-www-form-urlencoded / multipart) er allerede konsumert
        // av ASP.NET sin form-parser, så den må hentes via Request.Form — ikke fra Request.Body.
        // Tåler gzip-komprimert body (Content-Encoding: gzip).
        string raw;
        Dictionary<string, string>? skjema = null;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            skjema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in form) if (!skjema.ContainsKey(f.Key)) skjema[f.Key] = f.Value.ToString();
            raw = string.Join("&", form.Select(f => f.Key + "=" + f.Value));
        }
        else
        {
            Stream kropp = Request.Body;
            if (Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
                kropp = new System.IO.Compression.GZipStream(kropp, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(kropp, Encoding.UTF8);
            raw = await reader.ReadToEndAsync();
        }

        // Token: query (?token=), sti (/inn/{token}), header X-Sms-Token eller skjemafelt «token».
        // Konstant-tids sammenligning.
        var forventet = await _settings.GetAsync(KeyToken);
        var presentert = Foerste(new[] { token, tokenSti, Request.Headers["X-Sms-Token"].ToString(),
            skjema is not null && skjema.TryGetValue("token", out var ft) ? ft : null });
        var hvordan = $"{Request.Method} {Request.ContentType ?? "(ingen content-type)"}";
        if (string.IsNullOrWhiteSpace(forventet))
        {
            await Logg("Avvist: ingen token konfigurert", $"{hvordan} · {Kort(raw)}");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Inbound SMS er ikke aktivert (mangler token)." });
        }
        if (!KonstantLik(forventet, presentert))
        {
            await Logg("Avvist: ugyldig token", $"{hvordan} · token={(string.IsNullOrWhiteSpace(presentert) ? "(mangler)" : "feil")} · {Kort(raw)}");
            return Unauthorized(new { error = "Ugyldig token." });
        }

        // Skjema: bruk de ferdig-dekodede feltene direkte (rå-strengen er kun for logging) + query som fallback.
        var felt = skjema is not null ? ParseFelt("", Request) : ParseFelt(raw, Request);
        if (skjema is not null) foreach (var (k, v) in skjema) felt[k] = v;
        // LinkMobility MO: gateway-push (skjema) bruker SND / RCV / TXT (+ ID, NET); JSON/XML-varianten bruker
        // source / destination / content.userData. Øvrige navn = varianter.
        var fra = Foerste(felt, "snd", "source", "sender", "from", "msisdn", "originator", "sourceaddr", "sourcenumber",
            "sourceaddress", "senderaddress", "sendernumber", "fromnumber", "sourcemsisdn", "phonenumber", "mobilenumber");
        var til = Foerste(felt, "rcv", "destination", "receiver", "to", "shortnumber", "destinationaddr", "destinationaddress",
            "recipientaddress", "recipient", "tonumber");
        var tekst = Foerste(felt, "txt", "userdata", "text", "message", "messagetext", "body", "sms", "smstext", "content");

        // Siste utvei: ukjente feltnavn — match på nøkkel-delstreng (også sti-prefiksede nøkler som
        // «sender_number»/«from_address»), og for numre krev at verdien faktisk ser ut som et telefonnummer.
        fra ??= PaaNokkel(felt, new[] { "source", "sender", "from", "msisdn", "orig", "anumber", "a_number", "caller" }, telefon: true);
        til ??= PaaNokkel(felt, new[] { "dest", "recip", "shortnumber", "shortcode", "bnumber", "b_number", "_to", "to_" }, telefon: true);
        tekst ??= PaaNokkel(felt, new[] { "userdata", "text", "message", "body", "content", "sms" }, telefon: false);

        if (string.IsNullOrWhiteSpace(fra))
        {
            var ct = Request.ContentType ?? "(ingen content-type)";
            await Logg("Mottatt, men fant ikke avsender", $"{hvordan} · felt=[{string.Join(",", felt.Keys)}] · {Kort(raw)}");
            _log.LogWarning("Innkommende SMS: fant ikke avsendernummer. Content-Type={Ct}. Felt=[{Felt}]. Rå: {Raw}", ct, string.Join(",", felt.Keys), Kort(raw));
            // Returner hva vi faktisk så, så leverandørens utvikler ser feilen med en gang.
            return BadRequest(new
            {
                error = "Mangler avsendernummer. Forventer «SND» (gateway-skjema) eller «source» (ev. sender/from/msisdn) i JSON, XML, skjema eller query.",
                contentType = ct,
                feltFunnet = felt.Keys.Where(k => !k.Equals("token", StringComparison.OrdinalIgnoreCase)).ToArray(),
            });
        }

        var rad = await _inn.MottaAsync(fra!, til, tekst ?? "", Kort(raw, 4000));
        await Logg("OK mottatt", $"fra={fra} · sak={(rad.KundekortId?.ToString() ?? "ingen match")} · «{Kort(tekst, 60)}»");
        _log.LogInformation("Innkommende SMS mottatt fra {Fra} (sak: {Sak}).", fra, rad.KundekortId?.ToString() ?? "ingen match");
        return Ok(new { ok = true, matchetSak = rad.KundekortId });
    }

    // ---- Hjelpere ----

    // Alle treff logges både i minnet (Admin → LinkMobility → Webhook-treff) og varig under
    // Hendelser, så vi kan se hva som faktisk kom inn også etter en deploy/omstart.
    private async Task Logg(string utfall, string detalj)
    {
        _debug.Logg(utfall, detalj);
        try { await _events.LogAsync("SMS inn", utfall + " · " + Kort(detalj, 300), "LinkMobility MO"); }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke logge SMS-inn-hendelse"); }
    }

    private static string? Foerste(IEnumerable<string?> kandidater) =>
        kandidater.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))?.Trim();

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

    // Nøkler som aldri er avsender/mottaker/tekst (id-er, tidsstempler, rutingmetadata, vår egen token).
    private static readonly string[] IgnorerNokler =
        { "token", "id", "timestamp", "operator", "route", "gate", "platform", "encoding", "type", "queued", "reference", "keyword", "range" };

    /// <summary>Finn første verdi der nøkkelen inneholder en av delstrengene. For numre må verdien se ut som
    /// et telefonnummer (8–15 siffer, ev. ledende +). Nøkler med id/timestamp/rutingmetadata hoppes over.</summary>
    private static string? PaaNokkel(Dictionary<string, string> felt, string[] delstrenger, bool telefon)
    {
        foreach (var (nokkel, verdi) in felt)
        {
            var k = nokkel.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(verdi) || IgnorerNokler.Any(k.Contains)) continue;
            if (!delstrenger.Any(k.Contains)) continue;
            if (telefon && !ErTelefon(verdi)) continue;
            return verdi.Trim();
        }
        return null;
    }

    private static bool ErTelefon(string s)
    {
        var siffer = s.Count(char.IsDigit);
        return siffer is >= 8 and <= 15 && s.Trim().All(c => char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')');
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
            else if (trimmet.StartsWith("<"))
            {
                // LinkMobility kan levere MO som XML (konfigureres hos Support). Flat ut elementnavn → verdi.
                try { FlatXml(XDocument.Parse(raw).Root, d); }
                catch { /* ikke gyldig XML — prøv skjema under */ }
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

    // XML: elementer med underelementer flates ut rekursivt; blad-elementer og attributter blir nøkkel → verdi.
    private static void FlatXml(XElement? el, Dictionary<string, string> d, string prefiks = "")
    {
        if (el is null) return;
        var navn = el.Name.LocalName;
        foreach (var a in el.Attributes())
        {
            if (!d.ContainsKey(a.Name.LocalName)) d[a.Name.LocalName] = a.Value;
            if (!d.ContainsKey(prefiks + navn + "_" + a.Name.LocalName)) d[prefiks + navn + "_" + a.Name.LocalName] = a.Value;
        }
        if (el.HasElements)
        {
            foreach (var barn in el.Elements()) FlatXml(barn, d, prefiks + navn + "_");
        }
        else
        {
            if (!d.ContainsKey(navn)) d[navn] = el.Value;
            if (prefiks.Length > 0 && !d.ContainsKey(prefiks + navn)) d[prefiks + navn] = el.Value;
        }
    }

    // Flater ut JSON. Hver bladverdi legges inn BÅDE som bar nøkkel («number») og sti-prefikset
    // («sender_number»), så nøstede strukturer kan matches på kontekst. Første verdi vinner.
    private static void Flat(JsonElement el, Dictionary<string, string> d, string prefiks = "")
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Flat(p.Value, d, prefiks + p.Name + "_");
                    else
                    {
                        var v = p.Value.ToString();
                        if (!d.ContainsKey(p.Name)) d[p.Name] = v;
                        if (prefiks.Length > 0 && !d.ContainsKey(prefiks + p.Name)) d[prefiks + p.Name] = v;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Flat(item, d, prefiks);
                break;
        }
    }

    private static string Kort(string? s, int maks = 500)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > maks ? s[..maks] : s);
}
