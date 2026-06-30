using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly WebhookService _hooks;
    private readonly KundekortService _kundekort;
    private readonly EventService _events;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(WebhookService hooks, KundekortService kundekort, EventService events,
        IWebHostEnvironment env, ILogger<WebhookController> log)
    {
        _hooks = hooks;
        _kundekort = kundekort;
        _events = events;
        _env = env;
        _log = log;
    }

    /// <summary>
    /// Inbound webhook for leads. Mottar finansielle data + NIN → mapper til kundekort.
    /// Fleksibel mapping: gjenkjenner mange feltnavn-varianter (norsk/engelsk).
    /// Sikkerhet: HTTPS påkrevd + Bearer-token (konstant-tids sammenligning).
    /// </summary>
    [HttpPost("soknad")]
    public async Task<IActionResult> Soknad([FromBody] JsonElement body)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        var token = ExtractToken();
        var hook = await _hooks.ValidateTokenAsync(token);
        if (hook is null)
        {
            _log.LogWarning("Webhook avvist: ugyldig/manglende token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig token." });
        }

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!WebhookService.IpAllowed(hook, clientIp))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "IP ikke tillatt." });

        // Støtt både ett objekt og en liste av leads.
        var elements = body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().ToList()
            : new List<JsonElement> { body };

        var opprettet = 0;
        string? sisteInfo = null;
        var feltLogget = false;

        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var flat = Flatten(el);

            // Logg hvilke felt som kommer inn (kun navn, ingen verdier/PII) — for verifisering.
            if (!feltLogget)
            {
                await _events.LogAsync("Webhook RAW", "Mottatte felt: " + string.Join(", ", flat.Keys.Take(40)), hook.Name);
                feltLogget = true;
            }

            var k = MapFlexible(flat);
            k.Kilde = KildeLabel(hook.Name);
            var (ok, error) = await _kundekort.SaveAsync(k);
            if (!ok) { _log.LogWarning("Webhook-lead avvist: {Error}", error); continue; }

            opprettet++;
            var belop = k.OnsketLaanebelop.HasValue ? $" · {k.OnsketLaanebelop:N0} kr" : "";
            sisteInfo = $"{k.KundeType} · {k.Laanetype ?? "—"}{belop}";
        }

        if (opprettet == 0)
            return BadRequest(new { error = "Ingen gyldige leads i payload." });

        await _hooks.RecordReceiptAsync(hook, sisteInfo ?? $"{opprettet} lead(s)");
        await _events.LogAsync("Webhook", $"{opprettet} lead(s) mottatt ({sisteInfo})", hook.Name);
        return Ok(new { status = "mottatt", opprettet });
    }

    private static string KildeLabel(string hookName) => hookName switch
    {
        WebhookService.PrismatchName => "Prismatch",
        WebhookService.InboundName => "Rentekutt.no",
        _ => hookName,
    };

    private string? ExtractToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var custom = Request.Headers["X-Webhook-Token"].ToString();
        return string.IsNullOrWhiteSpace(custom) ? null : custom.Trim();
    }

    // ---------- Fleksibel mapping ----------

    /// <summary>Flater ut JSON (også nøstet) til normaliserte feltnavn → verdi.</summary>
    private static Dictionary<string, string> Flatten(JsonElement el)
    {
        var dict = new Dictionary<string, string>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Object: Walk(p.Value); break;
                    case JsonValueKind.String: dict.TryAdd(Norm(p.Name), p.Value.GetString() ?? ""); break;
                    case JsonValueKind.Number: dict.TryAdd(Norm(p.Name), p.Value.GetRawText()); break;
                    case JsonValueKind.True:
                    case JsonValueKind.False: dict.TryAdd(Norm(p.Name), p.Value.GetRawText()); break;
                }
            }
        }
        Walk(el);
        return dict;
    }

    /// <summary>Normaliserer feltnavn: folder æøå, lowercaser, fjerner ikke-alfanumeriske.</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            switch (c)
            {
                case 'å': case 'â': sb.Append('a'); break;
                case 'ø': case 'ö': sb.Append('o'); break;
                case 'æ': sb.Append("ae"); break;
                default: if (char.IsLetterOrDigit(c)) sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string? Get(Dictionary<string, string> f, params string[] keys)
    {
        foreach (var k in keys)
            if (f.TryGetValue(Norm(k), out var v) && !string.IsNullOrWhiteSpace(v) && v.ToLowerInvariant() != "null")
                return v.Trim();
        return null;
    }

    private static decimal? GetDec(Dictionary<string, string> f, params string[] keys)
    {
        var v = Get(f, keys);
        if (v is null) return null;
        var clean = new string(v.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray()).Replace(" ", "");
        if (clean.Contains(',') && !clean.Contains('.')) clean = clean.Replace(',', '.');
        else clean = clean.Replace(",", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int? GetInt(Dictionary<string, string> f, params string[] keys)
        => GetDec(f, keys) is { } d ? (int)d : null;

    private static bool GetBool(Dictionary<string, string> f, params string[] keys)
    {
        var v = Get(f, keys)?.ToLowerInvariant();
        return v is "true" or "ja" or "yes" or "1";
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private static Kundekort MapFlexible(Dictionary<string, string> f)
    {
        var typeRaw = Get(f, "kunde_type", "lead_type", "type", "kundetype") ?? "B2C";
        var type = typeRaw.ToUpperInvariant().Contains("B2B") ? "B2B" : "B2C";

        var fnr = Get(f, "fodselsnummer", "fnr", "personnummer", "nin", "ssn");
        var org = Get(f, "orgnr", "organisasjonsnummer", "orgnummer");
        var mobil = Get(f, "mobilnummer", "mobil", "phone", "telefon", "tlf", "phonenumber");

        string id;
        if (type == "B2B" && !string.IsNullOrWhiteSpace(org) && org != "0") id = org;
        else if (!string.IsNullOrWhiteSpace(fnr)) id = fnr;
        else id = Digits(mobil); // tom → SaveAsync genererer fallback

        return new Kundekort
        {
            KundeType = type,
            KundeId = id,
            Foedselsnummer = fnr,
            FulltNavn = Get(f, "fullt_navn", "navn", "name", "fullname", "kundenavn", "company_name", "companyname"),
            Mobilnummer = mobil,
            Epost = Get(f, "epost", "email", "mail", "e_post"),
            Adresse = Get(f, "adresse", "address", "gateadresse"),
            Postnummer = Get(f, "postnummer", "postnr", "zip", "zipcode", "postalcode"),
            Poststed = Get(f, "poststed", "city", "sted", "by"),
            Kommune = Get(f, "kommune", "municipality"),
            Statsborgerskap = Get(f, "statsborgerskap", "citizenship"),
            Sivilstatus = Get(f, "sivilstatus", "maritalstatus"),
            AntallBarnUnder18 = GetInt(f, "antall_barn_under_18", "antallbarn", "barn", "children"),
            Boforhold = Get(f, "boforhold", "housing"),
            Arbeidssituasjon = Get(f, "arbeidssituasjon", "employment", "ansettelse"),
            Arbeidsgiver = Get(f, "arbeidsgiver", "employer"),
            AarsinntektBrutto = GetDec(f, "aarsinntekt_brutto", "aarsinntekt", "arsinntekt", "inntekt", "income", "annualincome"),
            BoligkostnadMnd = GetDec(f, "boligkostnad_mnd", "boligkostnad", "husleie", "rent"),
            Boliggjeld = GetDec(f, "boliggjeld", "mortgage"),
            Studielaan = GetDec(f, "studielaan", "studielan", "studentloan"),
            Billaan = GetDec(f, "billaan", "bilan", "carloan"),
            Forbruksgjeld = GetDec(f, "forbruksgjeld", "forbrukslaan", "consumerdebt", "kredittkort"),
            RefinansieresBelop = GetDec(f, "refinansieres_belop", "refinansiering", "refinance"),
            AktivInkasso = GetBool(f, "aktiv_inkasso", "inkasso", "debtcollection"),
            OnsketLaanebelop = GetDec(f, "onsket_laanebelop", "sum_laan", "sum_lan", "laanebelop", "lanebelop", "belop", "amount", "loanamount", "sum", "lanesum"),
            OnsketLopetidMnd = GetInt(f, "onsket_lopetid_mnd", "lopetid", "nedbetalingstid", "term"),
            Laanetype = Get(f, "laanetype", "lanetype", "loantype", "formaal"),
            NavarendeBank = Get(f, "navarende_bank", "naavaerende_bank", "nåværende_bank", "currentbank", "bank"),
            Kontonummer = Get(f, "kontonummer", "konto", "accountnumber"),
            Status = "Ny",
        };
    }
}
