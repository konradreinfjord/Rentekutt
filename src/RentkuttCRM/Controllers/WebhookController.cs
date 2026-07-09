using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly WebhookService _hooks;
    private readonly KundekortService _kundekort;
    private readonly EventService _events;
    private readonly SmsMalService _sms;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(WebhookService hooks, KundekortService kundekort, EventService events,
        SmsMalService sms, IWebHostEnvironment env, ILogger<WebhookController> log)
    {
        _hooks = hooks;
        _kundekort = kundekort;
        _events = events;
        _sms = sms;
        _env = env;
        _log = log;
    }

    /// <summary>
    /// Inbound webhook for leads. Mottar finansielle data + NIN → mapper til kundekort.
    /// Fleksibel mapping: gjenkjenner mange feltnavn-varianter (norsk/engelsk).
    /// Sikkerhet: HTTPS påkrevd + Bearer-token (konstant-tids sammenligning).
    /// </summary>
    [HttpPost("soknad")]
    [EnableRateLimiting("webhook")]
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

            await _sms.MaybeSendAutomatikkAsync(k);   // auto-SMS til kunde hvis slått på
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

    /// <summary>
    /// Flater ut JSON (også nøstet) til normaliserte feltnavn → verdi.
    /// For nøstede objekter legges BÅDE en sti-prefikset nøkkel (f.eks. medsoeker_fodselsnummer)
    /// OG den bare nøkkelen (fodselsnummer). Første bare-verdi vinner, så toppnivå/søker beholdes
    /// mens medsøker-felt fortsatt er tilgjengelig via prefikset nøkkel (unngår kollisjon).
    /// </summary>
    private static Dictionary<string, string> Flatten(JsonElement el)
    {
        var dict = new Dictionary<string, string>();
        void Walk(JsonElement e, string prefix)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject())
            {
                var norm = Norm(p.Name);
                var pathKey = prefix.Length == 0 ? norm : prefix + "_" + norm;
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Object:
                        Walk(p.Value, pathKey);
                        break;
                    case JsonValueKind.String:
                        Add(pathKey, norm, p.Value.GetString() ?? "");
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        Add(pathKey, norm, p.Value.GetRawText());
                        break;
                }
            }
        }
        void Add(string pathKey, string bare, string val)
        {
            dict[pathKey] = val;      // sti-prefikset nøkkel er alltid entydig
            dict.TryAdd(bare, val);   // bar nøkkel: første vinner (toppnivå/søker)
        }
        Walk(el, "");
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

        // Orgnr lagres i eget felt (kun for B2B, og kun når det er et reelt nummer).
        var orgnr = type == "B2B" && !string.IsNullOrWhiteSpace(org) && org != "0" ? Digits(org) : null;

        string id;
        if (!string.IsNullOrWhiteSpace(orgnr)) id = orgnr;   // kunde_id speiler orgnr for gruppering
        else if (!string.IsNullOrWhiteSpace(fnr)) id = fnr;
        else id = Digits(mobil); // tom → SaveAsync genererer fallback

        // Enhetskonvertering: payload sender år, kundekort lagrer måneder.
        var ansiennitetMnd = GetInt(f, "ansiennitet_mnd") ?? (GetInt(f, "ansiennitet_aar", "ansiennitetaar") is { } ay ? ay * 12 : (int?)null);
        var botidMnd = GetInt(f, "botid_mnd") ?? (GetDec(f, "botid_naavaerende_adresse_aar", "botid_aar") is { } ba ? (int)(ba * 12) : (int?)null);
        var lopetidMnd = GetInt(f, "onsket_lopetid_mnd", "lopetid", "nedbetalingstid", "term")
                         ?? (GetInt(f, "onsket_nedbetalingstid_aar") is { } na ? na * 12 : (int?)null);

        var medsokerFnr = Get(f, "medsoeker_fodselsnummer", "medsoker_fodselsnummer");
        var harMedsoker = GetBool(f, "medsoeker_har_medsoeker", "har_medsoeker", "har_medsoker")
                          || !string.IsNullOrWhiteSpace(medsokerFnr)
                          || !string.IsNullOrWhiteSpace(Get(f, "medsoeker_fullt_navn"));

        return new Kundekort
        {
            KundeType = type,
            KundeId = id,
            Orgnr = orgnr,
            Foedselsnummer = fnr,
            FulltNavn = Get(f, "fullt_navn", "navn", "name", "fullname", "kundenavn", "company_name", "companyname"),
            Mobilnummer = mobil,
            Epost = Get(f, "epost", "email", "mail", "e_post"),
            Adresse = Get(f, "adresse", "address", "gateadresse"),
            Postnummer = Get(f, "postnummer", "postnr", "zip", "zipcode", "postalcode"),
            Poststed = Get(f, "poststed", "city", "sted", "by"),
            Kommune = Get(f, "kommune", "municipality"),

            // Husholdning
            Statsborgerskap = Get(f, "statsborgerskap", "citizenship"),
            StatsborgerskapKode = Get(f, "statsborgerskap_kode"),
            Opprinnelsesland = Get(f, "opprinnelsesland"),
            AarBoddINorge = GetInt(f, "antall_aar_bodd_i_norge", "aar_bodd_i_norge"),
            Sivilstatus = Get(f, "sivilstatus", "maritalstatus"),
            SivilstatusKode = Get(f, "sivilstatus_kode"),
            AntallBarnUnder18 = GetInt(f, "antall_barn_under_18", "antallbarn", "barn", "children"),
            Boforhold = Get(f, "boforhold", "housing"),
            BoforholdKode = Get(f, "boforhold_kode"),
            BotidMnd = botidMnd,
            AntallBiler = GetInt(f, "antall_biler", "antallbiler", "cars"),

            // Arbeid og inntekt
            Arbeidssituasjon = Get(f, "arbeidssituasjon", "employment", "ansettelse"),
            ArbeidssituasjonKode = Get(f, "arbeidssituasjon_kode"),
            Arbeidsgiver = Get(f, "arbeidsgiver", "employer"),
            AnsiennitetMnd = ansiennitetMnd,
            Utdanning = Get(f, "utdanning", "education"),
            UtdanningKode = Get(f, "utdanning_kode"),
            AarsinntektBrutto = GetDec(f, "aarsinntekt_brutto", "aarsinntekt", "arsinntekt", "inntekt", "income", "annualincome"),
            HarAndreInntekter = GetBool(f, "har_andre_inntekter"),
            AndreInntekter = GetDec(f, "andre_inntekter"),
            HarEktefelleSamboerInntekt = GetBool(f, "har_ektefelle_samboer_inntekt"),
            EktefelleInntekt = GetDec(f, "ektefelle_samboer_aarsinntekt", "ektefelle_inntekt"),
            BoligkostnadMnd = GetDec(f, "boligkostnad_mnd", "boligkostnad", "husleie", "rent"),
            BetalerBarnebidrag = GetBool(f, "betaler_barnebidrag"),
            BarnebidragBetaltMnd = GetDec(f, "barnebidrag_betalt_mnd"),

            // Gjeld
            Boliggjeld = GetDec(f, "boliggjeld", "mortgage"),
            Studielaan = GetDec(f, "studielaan", "studielan", "studentloan"),
            Billaan = GetDec(f, "billaan", "bilan", "carloan"),
            Forbruksgjeld = GetDec(f, "forbruksgjeld", "forbrukslaan", "consumerdebt", "kredittkort"),
            SamletGjeld = GetDec(f, "samlet_gjeld"),
            RefinansieresBelop = GetDec(f, "refinansieres_belop", "refinansiering", "refinance"),
            AktivInkasso = GetBool(f, "aktiv_inkasso", "inkasso", "debtcollection"),

            // Lånedetaljer
            OnsketLaanebelop = GetDec(f, "onsket_laanebelop", "sum_laan", "sum_lan", "laanebelop", "lanebelop", "belop", "amount", "loanamount", "sum", "lanesum"),
            OnsketLopetidMnd = lopetidMnd,
            Laanetype = Get(f, "laanetype", "lanetype", "loantype"),
            Laaneformal = Get(f, "laaneformal", "formaal"),
            LaaneformalKode = Get(f, "laaneformal_kode"),
            NaavaerendeRente = GetDec(f, "naavaerende_rente", "nåværende rente på boliglån", "nåværende rente boliglån",
                "boliglånsrente", "nåværende boliglånsrente", "rentesats", "nominell rente", "rente"),
            NavarendeBank = Get(f, "navarende_bank", "naavaerende_bank", "nåværende_bank", "currentbank", "bank"),
            Kontonummer = Get(f, "kontonummer", "konto", "accountnumber"),

            // Medsøker (sti-prefikset for å unngå kollisjon med søker)
            HarMedsoker = harMedsoker,
            MedsokerNavn = Get(f, "medsoeker_fullt_navn", "medsoker_navn"),
            MedsokerFoedselsnummer = medsokerFnr,
            MedsokerMobil = Get(f, "medsoeker_mobilnummer", "medsoker_mobil"),
            MedsokerEpost = Get(f, "medsoeker_epost"),
            MedsokerAdresse = Get(f, "medsoeker_adresse"),
            MedsokerPostnummer = Get(f, "medsoeker_postnummer"),
            MedsokerPoststed = Get(f, "medsoeker_poststed"),
            MedsokerInntekt = GetDec(f, "medsoeker_aarsinntekt", "medsoker_inntekt"),
            MedsokerArbeidsforhold = Get(f, "medsoeker_arbeidssituasjon"),
            MedsokerArbeidssituasjonKode = Get(f, "medsoeker_arbeidssituasjon_kode"),

            // Skjema / tjeneste / samtykke
            Tjeneste = Get(f, "tjeneste"),
            TjenesteKode = Get(f, "tjeneste_kode"),
            SkjemaVersjon = GetInt(f, "skjema_versjon"),
            SamtykkeGjeldsregisterKredittsjekk = GetBool(f, "samtykke_gjeldsregister_og_kredittsjekk", "samtykke_gjeldsregister_og_kredittsjekk"),

            Status = "Åpen",
        };
    }
}
