using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentkuttCRM.Models.PartnerApi;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Rentekutt Partner API — for bankpartnere som mottar søknader fra Rentekutt.
///
/// Innlogging: banken kaller <c>POST /api/partner/auth/token</c> med brukernavn og passord (som
/// Rentekutt utleverer) og får en <b>tidsbestemt token</b> (JWT, gyldig 1 time). Tokenet sendes som
/// <c>Authorization: Bearer &lt;token&gt;</c> på de øvrige kallene.
///
/// Flyt: Rentekutt sender en søknad (<see cref="SoknadDto"/>) til bankens mottaks-endepunkt. Banken
/// melder statusendringer tilbake via <c>POST /api/partner/status</c>. Alle beløp er i NOK.
/// </summary>
[ApiController]
[Route("api/partner/v1")]
[ApiExplorerSettings(GroupName = "partner")]
[Produces("application/json")]
[Consumes("application/json")]
public class PartnerApiController : ControllerBase
{
    public const string KeyUser = "partner_api_user";       // brukernavn banken logger inn med
    public const string KeyPass = "partner_api_pass";       // passord banken logger inn med
    public const string KeySecret = "partner_api_secret";   // HMAC-signeringsnøkkel for token (auto-generert)
    private const int TokenLevetidSek = 3600;               // 1 time

    private readonly SettingsService _settings;
    private readonly KundekortService _kundekort;
    private readonly LoggService _logg;
    private readonly ILogger<PartnerApiController> _log;

    public PartnerApiController(SettingsService settings, KundekortService kundekort, LoggService logg, ILogger<PartnerApiController> log)
    {
        _settings = settings;
        _kundekort = kundekort;
        _logg = logg;
        _log = log;
    }

    /// <summary>
    /// Logg inn og få en tidsbestemt token. Oppgi brukernavn og passord (utlevert av Rentekutt).
    /// Svaret inneholder <c>access_token</c> som skal sendes som «Authorization: Bearer …» på de
    /// øvrige kallene. Tokenet er gyldig i 1 time.
    /// </summary>
    /// <response code="200">Innlogging OK — token returneres.</response>
    /// <response code="401">Feil brukernavn eller passord.</response>
    [HttpPost("auth/token")]
    [ProducesResponseType(typeof(TokenSvar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Token([FromBody] PartnerLoginDto paalogging)
    {
        var bruker = await _settings.GetAsync(KeyUser);
        var passord = await _settings.GetAsync(KeyPass);
        var secret = await _settings.GetAsync(KeySecret);
        if (string.IsNullOrWhiteSpace(bruker) || string.IsNullOrWhiteSpace(passord) || string.IsNullOrWhiteSpace(secret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Partner-API er ikke aktivert." });

        if (paalogging is null
            || !KonstantLik(bruker, paalogging.Username)
            || !KonstantLik(passord, paalogging.Password))
            return Unauthorized(new { error = "Feil brukernavn eller passord." });

        var token = LagToken(bruker!, secret!, TokenLevetidSek);
        return Ok(new TokenSvar { AccessToken = token, TokenType = "Bearer", ExpiresIn = TokenLevetidSek });
    }

    /// <summary>
    /// Eksempel på en søknad slik Rentekutt sender den. Bruk responsen som mal for mottaket. Krever token.
    /// </summary>
    [HttpGet("soknad/eksempel")]
    [ProducesResponseType(typeof(SoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SoknadEksempel()
        => await TokenOk() ? Ok(Eksempel()) : Unauthorized(new { error = "Mangler/ugyldig token." });

    /// <summary>
    /// Bankens status-tilbakemelding på en søknad. Krever token (<c>Authorization: Bearer …</c>).
    /// </summary>
    /// <param name="melding">Statusoppdatering knyttet til søknadens referanse.</param>
    /// <response code="200">Statusen er mottatt og registrert.</response>
    /// <response code="400">Mangler hendelseId, referanse eller status.</response>
    /// <response code="401">Mangler eller ugyldig token.</response>
    /// <response code="404">Ukjent referanse (søknaden finnes ikke hos Rentekutt).</response>
    [HttpPost("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status([FromBody] StatusCallbackDto melding)
    {
        if (!await TokenOk()) return Unauthorized(new { error = "Mangler/ugyldig token." });
        if (melding is null || string.IsNullOrWhiteSpace(melding.HendelseId) || string.IsNullOrWhiteSpace(melding.Referanse))
            return BadRequest(new { error = "Mangler hendelseId, referanse eller status." });

        // Referansen må være en kjent søknad hos oss (= Rentekutt sin sak-id).
        if (!Guid.TryParse(melding.Referanse, out var sakId) || await _kundekort.GetAsync(sakId) is null)
            return NotFound(new { error = "Ukjent referanse." });

        var detalj = $"Partner-status: {melding.Status}"
            + (melding.Tilbud is { } t ? $" · tilbud {t.EffektivRente?.ToString() ?? "?"} % eff, mnd {t.MaanedsbelopKr?.ToString("N0") ?? "?"} kr" : "")
            + (melding.InnvilgetBelop is { } ib ? $" · innvilget {ib:N0} kr" : "")
            + (melding.UtbetaltBelop is { } ub ? $" · utbetalt {ub:N0} kr {(melding.UtbetaltDato is { } ud ? ud.ToString("dd.MM.yyyy") : "")}" : "")
            + (string.IsNullOrWhiteSpace(melding.Begrunnelse) ? "" : $" · {melding.Begrunnelse}")
            + (string.IsNullOrWhiteSpace(melding.BankReferanse) ? "" : $" · bankref {melding.BankReferanse}")
            + $" · hendelse {melding.HendelseId}";
        try { await _logg.LoggAsync(sakId, "Bankpartner", detalj, "bank"); }
        catch (Exception ex) { _log.LogWarning(ex, "Logging av partner-status feilet"); }

        _log.LogInformation("Partner-status mottatt: ref={Ref} status={Status} hendelse={H}", melding.Referanse, melding.Status, melding.HendelseId);
        return Ok(new { ok = true, referanse = melding.Referanse, status = melding.Status.ToString(), hendelseId = melding.HendelseId });
    }

    // ---- Token (JWT HS256, selvstendig) ----

    private async Task<bool> TokenOk()
    {
        var secret = await _settings.GetAsync(KeySecret);
        if (string.IsNullOrWhiteSpace(secret)) return false;
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return ValiderToken(auth["Bearer ".Length..].Trim(), secret!);
    }

    private static string LagToken(string sub, string secret, int levetidSek)
    {
        var naa = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "rentekutt", ["sub"] = sub, ["iat"] = naa, ["exp"] = naa + levetidSek,
        });
        var payload = B64Url(Encoding.UTF8.GetBytes(payloadJson));
        var sig = B64Url(Hmac(secret, $"{header}.{payload}"));
        return $"{header}.{payload}.{sig}";
    }

    private static bool ValiderToken(string token, string secret)
    {
        var deler = token.Split('.');
        if (deler.Length != 3) return false;
        var forventet = B64Url(Hmac(secret, $"{deler[0]}.{deler[1]}"));
        if (!KonstantLik(forventet, deler[2])) return false;
        try
        {
            var json = Encoding.UTF8.GetString(FromB64Url(deler[1]));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return false;
            return exp.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch { return false; }
    }

    private static byte[] Hmac(string secret, string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    private static bool KonstantLik(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static SoknadDto Eksempel() => new()
    {
        Referanse = "3f9a1c84-7b2e-4d56-9c0a-1e2f3a4b5c6d",
        Kundetype = Kundetype.Privat,
        Laanetype = Laanetype.Refinansiering,
        OnsketBelop = 350000,
        NedbetalingstidMnd = 60,
        RefinansieresBelop = 280000,
        Opprettet = new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc),
        Samtykke = new SamtykkeDto
        {
            SamtykkeId = "8c1e5f20-9a3b-4c7d-8e2f-1a2b3c4d5e6f",
            Tidspunkt = new DateTime(2026, 10, 19, 9, 28, 0, DateTimeKind.Utc),
            VilkaarVersjon = "2026-01",
        },
        Soker = new SokerDto
        {
            // Syntetisk Tenor-testnummer (måned +80) — tilhører ingen virkelig person.
            FulltNavn = "Ola Nordmann",
            Fodselsnummer = "15857243371",
            Epost = "ola.nordmann@example.com",
            Mobilnummer = "40412345",
            Adresse = "Storgata 1",
            Postnummer = "0155",
            Poststed = "Oslo",
            AarsinntektBrutto = 640000,
            Sivilstatus = Sivilstatus.Gift,
            Arbeidssituasjon = Arbeidssituasjon.FastAnsatt,
            Boforhold = Boforhold.Eier,
            AntallBarnUnder18 = 2,
            UsikretGjeld = 280000,
            Boliggjeld = 2400000,
            Studielaan = 120000,
            Arbeidsgiver = "Eksempel AS",
            AnsattSiden = new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            BoutgifterMnd = 14000,
            Statsborgerskap = "NO",
            BotidAar = 40,
            Pep = false,
        },
    };
}
