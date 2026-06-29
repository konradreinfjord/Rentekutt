using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly WebhookService _hooks;
    private readonly KundekortService _kundekort;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(WebhookService hooks, KundekortService kundekort,
        IWebHostEnvironment env, ILogger<WebhookController> log)
    {
        _hooks = hooks;
        _kundekort = kundekort;
        _env = env;
        _log = log;
    }

    /// <summary>
    /// Inbound webhook for leads. Mottar finansielle data + NIN → mapper til kundekort.
    /// Sikkerhet: HTTPS påkrevd + Bearer-token (konstant-tids sammenligning).
    /// </summary>
    [HttpPost("soknad")]
    public async Task<IActionResult> Soknad([FromBody] SoknadPayload payload)
    {
        // 1) Krev HTTPS (PII/finansiell data). Lokalt (Development) tillates http.
        if (!Request.IsHttps && !_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "HTTPS påkrevd." });

        // 2) Token (Authorization: Bearer <token>  eller  X-Webhook-Token)
        var token = ExtractToken();
        var hook = await _hooks.ValidateTokenAsync(token);
        if (hook is null)
        {
            _log.LogWarning("Webhook avvist: ugyldig/manglende token fra {IP}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Ugyldig token." });
        }

        // 2b) IP-whitelist (tom liste = alle tillatt)
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!WebhookService.IpAllowed(hook, clientIp))
        {
            _log.LogWarning("Webhook avvist: IP {IP} ikke i whitelist", clientIp);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "IP ikke tillatt." });
        }

        if (payload is null)
            return BadRequest(new { error = "Tomt payload." });

        // 3) Map → kundekort
        var k = payload.ToKundekort();
        var (ok, error) = await _kundekort.SaveAsync(k);
        if (!ok)
        {
            // Logg ALDRI fødselsnummer/PII. Kun feilårsak.
            _log.LogWarning("Webhook-lead avvist ved lagring: {Error}", error);
            return BadRequest(new { error });
        }

        // 4) Registrer sist mottatt (uten PII)
        var belop = k.OnsketLaanebelop.HasValue ? $" · {k.OnsketLaanebelop:N0} kr" : "";
        await _hooks.RecordReceiptAsync(hook, $"{k.KundeType} · {k.Laanetype ?? "—"}{belop}");

        _log.LogInformation("Webhook-lead mottatt og lagret ({Type})", k.KundeType);
        return Ok(new { status = "mottatt", kunde_type = k.KundeType });
    }

    private string? ExtractToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var custom = Request.Headers["X-Webhook-Token"].ToString();
        return string.IsNullOrWhiteSpace(custom) ? null : custom.Trim();
    }
}

/// <summary>Payload-format leverandøren sender til /api/webhook/soknad.</summary>
public class SoknadPayload
{
    [JsonPropertyName("kunde_type")] public string? KundeType { get; set; }
    [JsonPropertyName("fodselsnummer")] public string? Fodselsnummer { get; set; }
    [JsonPropertyName("orgnr")] public string? Orgnr { get; set; }

    [JsonPropertyName("fullt_navn")] public string? FulltNavn { get; set; }
    [JsonPropertyName("mobilnummer")] public string? Mobilnummer { get; set; }
    [JsonPropertyName("epost")] public string? Epost { get; set; }
    [JsonPropertyName("adresse")] public string? Adresse { get; set; }
    [JsonPropertyName("postnummer")] public string? Postnummer { get; set; }
    [JsonPropertyName("poststed")] public string? Poststed { get; set; }

    [JsonPropertyName("sivilstatus")] public string? Sivilstatus { get; set; }
    [JsonPropertyName("antall_barn_under_18")] public int? AntallBarnUnder18 { get; set; }
    [JsonPropertyName("boforhold")] public string? Boforhold { get; set; }

    [JsonPropertyName("arbeidssituasjon")] public string? Arbeidssituasjon { get; set; }
    [JsonPropertyName("arbeidsgiver")] public string? Arbeidsgiver { get; set; }
    [JsonPropertyName("aarsinntekt_brutto")] public decimal? AarsinntektBrutto { get; set; }
    [JsonPropertyName("andre_inntekter")] public decimal? AndreInntekter { get; set; }

    [JsonPropertyName("boligkostnad_mnd")] public decimal? BoligkostnadMnd { get; set; }

    [JsonPropertyName("boliggjeld")] public decimal? Boliggjeld { get; set; }
    [JsonPropertyName("studielaan")] public decimal? Studielaan { get; set; }
    [JsonPropertyName("billaan")] public decimal? Billaan { get; set; }
    [JsonPropertyName("forbruksgjeld")] public decimal? Forbruksgjeld { get; set; }
    [JsonPropertyName("refinansieres_belop")] public decimal? RefinansieresBelop { get; set; }
    [JsonPropertyName("aktiv_inkasso")] public bool? AktivInkasso { get; set; }

    [JsonPropertyName("onsket_laanebelop")] public decimal? OnsketLaanebelop { get; set; }
    [JsonPropertyName("onsket_lopetid_mnd")] public int? OnsketLopetidMnd { get; set; }
    [JsonPropertyName("laanetype")] public string? Laanetype { get; set; }

    [JsonPropertyName("kontonummer")] public string? Kontonummer { get; set; }

    public Kundekort ToKundekort()
    {
        var type = string.Equals(KundeType, "B2B", StringComparison.OrdinalIgnoreCase) ? "B2B" : "B2C";
        return new Kundekort
        {
            KundeType = type,
            KundeId = (type == "B2B" ? Orgnr : Fodselsnummer)?.Trim() ?? "",
            Foedselsnummer = Fodselsnummer,
            FulltNavn = FulltNavn,
            Mobilnummer = Mobilnummer,
            Epost = Epost,
            Adresse = Adresse,
            Postnummer = Postnummer,
            Poststed = Poststed,
            Sivilstatus = Sivilstatus,
            AntallBarnUnder18 = AntallBarnUnder18,
            Boforhold = Boforhold,
            Arbeidssituasjon = Arbeidssituasjon,
            Arbeidsgiver = Arbeidsgiver,
            AarsinntektBrutto = AarsinntektBrutto,
            AndreInntekter = AndreInntekter,
            BoligkostnadMnd = BoligkostnadMnd,
            Boliggjeld = Boliggjeld,
            Studielaan = Studielaan,
            Billaan = Billaan,
            Forbruksgjeld = Forbruksgjeld,
            RefinansieresBelop = RefinansieresBelop,
            AktivInkasso = AktivInkasso ?? false,
            OnsketLaanebelop = OnsketLaanebelop,
            OnsketLopetidMnd = OnsketLopetidMnd,
            Laanetype = Laanetype,
            Kontonummer = Kontonummer,
            Status = "Ny",
        };
    }
}
