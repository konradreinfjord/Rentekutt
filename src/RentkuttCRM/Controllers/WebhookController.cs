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
    private readonly SamtykkeService _samtykke;
    private readonly AlarmService _alarm;
    private readonly WebhookPayloadService _payloads;
    private readonly LoggService _logg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(WebhookService hooks, KundekortService kundekort, EventService events,
        SmsMalService sms, SamtykkeService samtykke, AlarmService alarm, WebhookPayloadService payloads,
        LoggService logg, IWebHostEnvironment env, ILogger<WebhookController> log)
    {
        _hooks = hooks;
        _kundekort = kundekort;
        _events = events;
        _sms = sms;
        _samtykke = samtykke;
        _alarm = alarm;
        _payloads = payloads;
        _logg = logg;
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
        if (hook is null || hook.Name == WebhookService.VippsName)
        {
            // Vipps-tokenet gjelder KUN /vipps-endepunktet, ikke søknadsmottak.
            _log.LogWarning("Webhook avvist: ugyldig/manglende token fra {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig token." });
        }

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!WebhookService.IpAllowed(hook, clientIp))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "IP ikke tillatt." });

        // Lagre rå payload (fnr maskert) uansett utfall — for feilsøking av siste 50.
        var raw = FnrRedactor.Redact(body.GetRawText());
        var opprettet = 0;
        string? sisteInfo = null;
        string? feil = null;
        string? feiletKontakt = null;   // navn/mobil på et lead som feilet — så det kan følges opp
        Guid? forsteId = null;

        try
        {
            // Robust: støtt både ett objekt og en liste; hopp over det som ikke er objekt.
            var elements = body.ValueKind == JsonValueKind.Array
                ? body.EnumerateArray().ToList()
                : new List<JsonElement> { body };
            var feltLogget = false;

            foreach (var el in elements)
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var flat = Flatten(el);

                if (!feltLogget)
                {
                    await _events.LogAsync("Webhook RAW", "Mottatte felt: " + string.Join(", ", flat.Keys.Take(40)), hook.Name);
                    feltLogget = true;
                }

                var k = MapFlexible(flat);
                k.Kilde = KildeLabel(hook.Name);

                // Prismatch-leads er forenklede, ueide leads (kontakt + grunnleggende lånedata) uten
                // samtykke/2FA. De settes i status «Nytt lead» til en rådgiver plukker dem.
                if (hook.Name == WebhookService.PrismatchName)
                    k.Status = KundekortService.StatusNyttLead;

                // MOD11-sjekk ved mottak brukes KUN til å flagge — vi avviser ALDRI leadet på grunn av
                // fødselsnummeret, så ingen søknad går tapt. Fnr lagres uansett som det er; bank-sending
                // validerer MOD11 og blokkerer der (med tydelig melding), så rådgiver kan rette først.
                var fnrUgyldig = !string.IsNullOrWhiteSpace(k.Foedselsnummer) && !Fnr.ErGyldig(k.Foedselsnummer);
                var medsokerFnrUgyldig = !string.IsNullOrWhiteSpace(k.MedsokerFoedselsnummer) && !Fnr.ErGyldig(k.MedsokerFoedselsnummer);

                // Match mot et påbegynt Vipps-utkast: KUN entydige kriterier (mobil → e-post; navn
                // brukes ikke). Treff = komplettér utkastet (samme rad) og løft status til «Åpen».
                // Ingen match → utkastet (om det finnes) blir stående som «Påbegynt søknad».
                var (utkast, matchFelt) = await _kundekort.FinnPaabegyntAsync(k.Mobilnummer, k.Epost);
                string? aktor = null;
                if (utkast is not null)
                {
                    k.Id = utkast.Id;
                    // Foretrekk Vipps/BankID-verifisert fnr når søknadens eget mangler ELLER er ugyldig —
                    // et verifisert nummer skal alltid vinne over et ugyldig fra skjemaet.
                    if ((string.IsNullOrWhiteSpace(k.Foedselsnummer) || fnrUgyldig) && !string.IsNullOrWhiteSpace(utkast.Foedselsnummer))
                    {
                        k.Foedselsnummer = utkast.Foedselsnummer;
                        if (!string.IsNullOrWhiteSpace(utkast.FnrKilde)) k.FnrKilde = utkast.FnrKilde;
                        fnrUgyldig = !Fnr.ErGyldig(k.Foedselsnummer);   // re-vurder etter overstyring
                    }
                    if (string.IsNullOrWhiteSpace(k.FulltNavn)) k.FulltNavn = utkast.FulltNavn;
                    if (string.IsNullOrWhiteSpace(k.Mobilnummer)) k.Mobilnummer = utkast.Mobilnummer;
                    if (string.IsNullOrWhiteSpace(k.Epost)) k.Epost = utkast.Epost;
                    aktor = "System (komplettert fra Vipps-utkast)";
                }

                var (ok, error) = await _kundekort.SaveAsync(k, aktor: aktor);
                if (!ok)
                {
                    feil = error;
                    // Fang kontaktinfo (uten fnr) så alarmen viser HVEM som feilet og kan følges opp manuelt.
                    var navn = string.IsNullOrWhiteSpace(k.FulltNavn) ? "(uten navn)" : k.FulltNavn;
                    var mob = string.IsNullOrWhiteSpace(k.Mobilnummer) ? "" : $" · {k.Mobilnummer}";
                    var ep = string.IsNullOrWhiteSpace(k.Epost) ? "" : $" · {k.Epost}";
                    feiletKontakt = $"{navn}{mob}{ep}";
                    _log.LogWarning("Webhook-lead avvist: {Error}", error);
                    continue;
                }

                // Flagg ugyldig fnr som et notat (uten selve nummeret) — leadet er lagret, men må rettes
                // før sending til bank. Bank-sending blokkerer uansett på MOD11.
                if (fnrUgyldig || medsokerFnrUgyldig)
                    await _logg.LoggAsync(k.Id, "System",
                        $"Fødselsnummer med feil kontrollsiffer mottatt{(medsokerFnrUgyldig ? " (medsøker)" : "")} — lagret som mottatt, må rettes før sending til bank.",
                        kategori: "advarsel");

                // Revisjonsspor per kobling: hvilket felt matchet (tidspunkt er automatisk). Ingen fnr i teksten.
                // Vipps-sesjon/ref er logget på samme sak ved opprettelse av utkastet.
                if (utkast is not null)
                    await _logg.LoggAsync(k.Id, "System",
                        $"Vipps-utkast koblet til søknad — matchet på {matchFelt}", kategori: "kobling");

                if (k.SamtykkeGjeldsregisterKredittsjekk && k.Id != Guid.Empty)
                    await _samtykke.RegistrerAsync(k.Id, SamtykkeService.FormaalKreditt, KildeLabel(hook.Name), tekstversjon: SamtykkeService.SamtykketekstVersjon, ip: clientIp);

                await _sms.MaybeSendAutomatikkAsync(k);
                opprettet++;
                forsteId ??= k.Id;
                var belop = k.OnsketLaanebelop.HasValue ? $" · {k.OnsketLaanebelop:N0} kr" : "";
                sisteInfo = $"{k.KundeType} · {k.Laanetype ?? "—"}{belop}";
            }
        }
        catch (Exception ex)
        {
            feil = ex.Message;
            _log.LogError(ex, "Webhook-behandling feilet");
        }

        var vellykket = opprettet > 0 && feil is null;
        await _payloads.LagreAsync(hook.Name, raw, vellykket,
            feil ?? (opprettet == 0 ? "Ingen gyldige leads i payload." : null), forsteId);

        if (!vellykket)
        {
            // Teknisk API-feil (unntak/DB-feil som PGRST204) er kritisk og skal synes tydelig.
            // «Ingen gyldige leads» er en advarsel. Begge får tidspunkt + detaljer i alarmen.
            var teknisk = feil is not null;
            var naa = DateTime.UtcNow.TilOslo().ToString("dd.MM.yyyy HH:mm:ss");
            var hvem = string.IsNullOrWhiteSpace(feiletKontakt) ? "" : $" Kunde: {feiletKontakt}.";
            await _alarm.RaiseAsync("API",
                teknisk ? $"API-feil ved mottak av søknad ({hook.Name})" : $"Søknad mottatt uten gyldige leads ({hook.Name})",
                $"Tidspunkt: {naa}.{hvem} {(feil ?? "Ingen gyldige leads i payload.")} — kjør på nytt fra Admin → Kanaler → siste payloads.",
                teknisk ? AlarmService.Alvorlighet.Kritisk : AlarmService.Alvorlighet.Advarsel,
                "API", teknisk ? $"api-feil-inbound-{hook.Name}" : $"webhook-tomt-{hook.Name}");
        }

        if (opprettet == 0)
            return BadRequest(new { error = feil ?? "Ingen gyldige leads i payload." });

        await _hooks.RecordReceiptAsync(hook, sisteInfo ?? $"{opprettet} lead(s)");
        await _events.LogAsync("Webhook", $"{opprettet} lead(s) mottatt ({sisteInfo})", hook.Name);
        return Ok(new { status = "mottatt", opprettet });
    }

    /// <summary>
    /// Vipps/BankID-bekreftelse. Når kunden autentiserer seg med Vipps opprettes et utkast
    /// (status «Påbegynt søknad») med navn/mobil/e-post (+ ev. fnr). Kompletteres senere når
    /// selve søknadsskjemaet kommer inn på /soknad (match på mobil → e-post → navn).
    /// Egen kanal/token — aksepterer KUN Vipps-webhooken.
    /// </summary>
    [HttpPost("vipps")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> Vipps([FromBody] JsonElement body)
        => HandleBekreftelseAsync(body, WebhookService.VippsName, "Vipps", KundekortService.FnrKildeVipps, "Vipps 2FA");

    /// <summary>
    /// BankID-bekreftelse. Samme oppbygning som Vipps: kunden autentiserer seg med BankID, det
    /// opprettes et utkast (status «Påbegynt søknad») med navn/mobil/e-post (+ ev. fnr fra BankID).
    /// Kompletteres/matches når selve søknadsskjemaet kommer inn på /soknad (mobil → e-post).
    /// Egen kanal/token/URL — aksepterer KUN BankID-webhooken.
    /// </summary>
    [HttpPost("bankid")]
    [EnableRateLimiting("webhook")]
    public Task<IActionResult> BankId([FromBody] JsonElement body)
        => HandleBekreftelseAsync(body, WebhookService.BankIdName, "BankID", KundekortService.FnrKildeBankId, "BankID 2FA");

    /// <summary>Felles behandling for Vipps- og BankID-bekreftelser (påbegynt søknad + samtykke via 2FA).</summary>
    private async Task<IActionResult> HandleBekreftelseAsync(JsonElement body, string webhookNavn, string kildeLabel, string fnrKilde, string samtykkeKilde)
    {
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        var token = ExtractToken();
        var hook = await _hooks.ValidateTokenAsync(token);
        if (hook is null || hook.Name != webhookNavn)
        {
            _log.LogWarning("{Kilde}-webhook avvist: ugyldig/manglende token fra {IP}", kildeLabel, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig token." });
        }

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!WebhookService.IpAllowed(hook, clientIp))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "IP ikke tillatt." });

        // Lagre rå payload (fnr maskert) uansett utfall — for feilsøking av siste 50.
        var raw = FnrRedactor.Redact(body.GetRawText());
        string? feil = null;
        Guid? id = null;
        try
        {
            // Robust: tåler ett objekt eller en liste; ta første objekt.
            var el = body.ValueKind == JsonValueKind.Array ? body.EnumerateArray().FirstOrDefault() : body;
            var flat = Flatten(el);
            var k = MapVipps(flat);
            k.FnrKilde = fnrKilde;
            // Autentisert med Vipps/BankID + 2FA → autentiseringen ER samtykkegrunnlaget for
            // gjeldsregister/kredittsjekk, så samtykke settes alltid (uavhengig av payload-flagget).
            k.SamtykkeGjeldsregisterKredittsjekk = true;
            // Subjekt-/sesjons-ID for revisjonssporet (ingen fnr).
            var sesjonsRef = Get(flat, "ordernumber", "sub", "subject", "sessionid", "session_id", "sid", "referanse") ?? "—";
            if (string.IsNullOrWhiteSpace(k.Mobilnummer) && string.IsNullOrWhiteSpace(k.Epost) && string.IsNullOrWhiteSpace(k.FulltNavn))
            {
                feil = $"{kildeLabel}-bekreftelsen mangler både mobil, e-post og navn — kan verken opprette eller matche.";
            }
            else
            {
                var (ok, error) = await _kundekort.SaveAsync(k, aktor: kildeLabel);
                if (!ok) feil = error ?? "Lagring av utkast feilet.";
                else
                {
                    id = k.Id;
                    if (k.Id != Guid.Empty)
                        await _samtykke.RegistrerAsync(k.Id, SamtykkeService.FormaalKreditt, samtykkeKilde, tekstversjon: SamtykkeService.SamtykketekstVersjon, ip: clientIp);
                    await _logg.LoggAsync(k.Id, kildeLabel,
                        $"Påbegynt søknad opprettet via {kildeLabel}-autentisering — subjekt/sesjon: {sesjonsRef}", kategori: "kobling");
                    await _hooks.RecordReceiptAsync(hook, $"{kildeLabel}-bekreftelse (påbegynt søknad)");
                    await _events.LogAsync(kildeLabel, $"Påbegynt søknad opprettet fra {kildeLabel}-bekreftelse", hook.Name);
                }
            }
        }
        catch (Exception ex)
        {
            feil = ex.Message;
            _log.LogError(ex, "{Kilde}-behandling feilet", kildeLabel);
        }

        await _payloads.LagreAsync(hook.Name, raw, feil is null, feil, id);
        if (feil is not null)
        {
            var naa = DateTime.UtcNow.TilOslo().ToString("dd.MM.yyyy HH:mm:ss");
            await _alarm.RaiseAsync("API", $"API-feil ved {kildeLabel}-bekreftelse",
                $"Tidspunkt: {naa}. {feil} — full payload i Admin → Kanaler.",
                AlarmService.Alvorlighet.Kritisk, "API", $"api-feil-{kildeLabel.ToLowerInvariant()}");
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = feil });
        }
        return Ok(new { status = "påbegynt", id });
    }

    internal static Kundekort MapVipps(Dictionary<string, string> f)
    {
        // Vipps-payload bruker CellPhone/FullName/Email/CustomerType/Address/ZipCode/CompanyName.
        var typeRaw = Get(f, "customertype", "kunde_type", "type", "kundetype") ?? "B2C";
        var type = typeRaw.ToUpperInvariant().Contains("B2B") ? "B2B" : "B2C";

        var mobil = Get(f, "cellphone", "mobilnummer", "mobil", "phone", "phonenumber", "telefon", "tlf", "phone_number", "mobilephone");
        var fnr = Get(f, "fodselsnummer", "fnr", "personnummer", "nin", "ssn", "nationalidentitynumber", "nationalidnumber");
        var epost = Get(f, "epost", "email", "mail", "e_post");
        var firmanavn = Get(f, "companyname", "company_name", "firmanavn");
        // For B2B foretrekkes firmanavn; ellers personnavn.
        var navn = (type == "B2B" ? firmanavn : null)
                   ?? Get(f, "fullt_navn", "navn", "name", "fullname") ?? firmanavn;
        if (string.IsNullOrWhiteSpace(navn))
        {
            var fornavn = Get(f, "fornavn", "given_name", "givenname", "firstname", "first_name");
            var etternavn = Get(f, "etternavn", "family_name", "familyname", "lastname", "last_name", "surname");
            var sammen = string.Join(" ", new[] { fornavn, etternavn }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(sammen)) navn = sammen;
        }

        // Adresse kan ha bolignummer på egen linje ("… 16C\nH0406") — samle til én lesbar linje.
        var adresse = Get(f, "adresse", "address", "gateadresse")?.Replace("\r", "").Replace("\n", ", ").Trim();
        var postnr = Get(f, "postnummer", "postnr", "zip", "zipcode", "postalcode");
        var ordre = Get(f, "ordernumber", "ordrenummer", "order_id", "orderid");

        return new Kundekort
        {
            KundeType = type,
            KundeId = !string.IsNullOrWhiteSpace(fnr) ? fnr : Digits(mobil),
            Foedselsnummer = fnr,
            FulltNavn = navn,
            Mobilnummer = mobil,
            Epost = epost,
            Adresse = adresse,
            Postnummer = postnr,   // SaveAsync/BerikGeografi fyller poststed/kommune/fylke fra postnr
            // Vipps-autentisering skjer på rentekutt.no → kilde = Rentekutt.no (ikke «Vipps»).
            // Selve autentiseringsmetoden dokumenteres i FnrKilde + revisjonssporet.
            Kilde = KildeLabel(WebhookService.InboundName),
            FnrKilde = KundekortService.FnrKildeVipps,
            Notater = string.IsNullOrWhiteSpace(ordre) ? null : $"Vipps ordrenr: {ordre}",
            SamtykkeGjeldsregisterKredittsjekk = GetBool(f, "samtykke_gjeldsregister_og_kredittsjekk", "samtykke"),
            Status = KundekortService.StatusPaabegynt,
        };
    }

    internal static string KildeLabel(string hookName) => hookName switch
    {
        WebhookService.PrismatchName => "Prismatch",
        WebhookService.InboundName => "Rentekutt.no",
        WebhookService.VippsName => "Vipps",
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
    internal static Dictionary<string, string> Flatten(JsonElement el)
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
    internal static string Norm(string s)
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

    internal static string? Get(Dictionary<string, string> f, params string[] keys)
    {
        foreach (var k in keys)
            if (f.TryGetValue(Norm(k), out var v) && !string.IsNullOrWhiteSpace(v) && v.ToLowerInvariant() != "null")
                return v.Trim();
        return null;
    }

    internal static decimal? GetDec(Dictionary<string, string> f, params string[] keys)
    {
        var v = Get(f, keys);
        if (v is null) return null;
        var clean = new string(v.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray()).Replace(" ", "");
        if (clean.Contains(',') && !clean.Contains('.')) clean = clean.Replace(',', '.');
        else clean = clean.Replace(",", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    internal static int? GetInt(Dictionary<string, string> f, params string[] keys)
        => GetDec(f, keys) is { } d ? (int)d : null;

    internal static bool GetBool(Dictionary<string, string> f, params string[] keys)
    {
        var v = Get(f, keys)?.ToLowerInvariant();
        return v is "true" or "ja" or "yes" or "1";
    }

    internal static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    internal static Kundekort MapFlexible(Dictionary<string, string> f)
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

        // Navn: for B2B er firmanavnet det primære (vises øverst), og personnavnet blir kontaktperson.
        // Fremtidige prismatch-payloads sender både company_name (firma) og fullt_navn (person) + orgnr.
        var personNavn = Get(f, "fullt_navn", "navn", "name", "fullname", "kundenavn");
        var firmaNavn = Get(f, "company_name", "companyname", "firmanavn", "selskapsnavn", "bedriftsnavn");
        var fulltNavn = type == "B2B" ? (firmaNavn ?? personNavn) : personNavn;
        // Kontaktperson kun for B2B, og kun når vi faktisk har et firmanavn å skille personen fra.
        var kontaktperson = type == "B2B" && !string.IsNullOrWhiteSpace(firmaNavn) ? personNavn : null;

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
            FulltNavn = fulltNavn,
            KontaktpersonNavn = kontaktperson,
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
            NavarendeBank = Get(f, "naavarende_bank", "navarende_bank", "naavaerende_bank", "nåværende_bank", "currentbank", "bank"),
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

            // Fnr oppgitt i søknadsskjema = lavere sikkerhetsnivå enn Vipps/BankID-autentisert.
            FnrKilde = KundekortService.FnrKildeSkjema,
            Status = KundekortService.StatusNySoknad,
        };
    }
}
