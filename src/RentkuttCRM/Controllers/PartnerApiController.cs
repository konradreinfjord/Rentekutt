using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentkuttCRM.Models.PartnerApi;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Endepunktene for bankpartnere. Logg inn på <c>/auth/token</c> for å få en token, hent et
/// søknadseksempel på <c>/soknad/eksempel</c>, og meld status tilbake på <c>/status</c>.
/// Se full forklaring øverst på siden. Alle beløp er i hele norske kroner.
/// </summary>
[ApiController]
[Route("api/partner/v1")]
[ApiExplorerSettings(GroupName = "partner")]
[Produces("application/json")]
[Consumes("application/json")]
public class PartnerApiController : ControllerBase
{
    private const int TokenLevetidSek = 3600;               // 1 time

    private readonly PartnerKredsService _kreds;
    private readonly KundekortService _kundekort;
    private readonly LoggService _logg;
    private readonly ILogger<PartnerApiController> _log;

    public PartnerApiController(PartnerKredsService kreds, KundekortService kundekort, LoggService logg, ILogger<PartnerApiController> log)
    {
        _kreds = kreds;
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
        if ((await _kreds.HentAsync()).Count == 0)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Partner-API er ikke aktivert." });

        var kreds = await _kreds.ValiderAsync(paalogging?.Username, paalogging?.Password);
        if (kreds is null) return Unauthorized(new { error = "Feil brukernavn eller passord." });

        var secret = await _kreds.SecretAsync();
        var token = LagToken(kreds.Username, kreds.Bank, secret, TokenLevetidSek);
        return Ok(new TokenSvar { AccessToken = token, TokenType = "Bearer", ExpiresIn = TokenLevetidSek });
    }

    /// <summary>
    /// Søknadene som er sendt til DIN bank (tokenet avgjør hvilken bank). Nyeste først, maks 100.
    /// </summary>
    [HttpGet("soknader")]
    [ProducesResponseType(typeof(List<SoknadDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Soknader()
    {
        var bank = await AutentisertBankAsync();
        if (bank is null) return Unauthorized(new { error = "Mangler/ugyldig token." });

        var soknader = (await _kundekort.ListLettAsync())
            .Where(k => string.Equals(k.DelegertBank, bank, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.CreatedAt)
            .Take(100)
            .Select(MapTilSoknad)
            .ToList();
        return Ok(soknader);
    }

    /// <summary>
    /// Eksempel på en søknad slik Rentekutt sender den. Bruk responsen som mal for mottaket. Krever token.
    /// </summary>
    [HttpGet("soknad/eksempel")]
    [ProducesResponseType(typeof(SoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SoknadEksempel()
        => await AutentisertBankAsync() is not null ? Ok(Eksempel()) : Unauthorized(new { error = "Mangler/ugyldig token." });

    /// <summary>
    /// Bankens status-tilbakemelding på en søknad. Krever token (<c>Authorization: Bearer …</c>).
    /// </summary>
    /// <param name="melding">Statusoppdatering knyttet til søknadens referanse.</param>
    /// <response code="200">Statusen er mottatt og registrert.</response>
    /// <response code="400">Mangler hendelseId, referanse eller status.</response>
    /// <response code="401">Mangler eller ugyldig token.</response>
    /// <response code="403">Søknaden tilhører en annen bank enn din.</response>
    /// <response code="404">Ukjent referanse (søknaden finnes ikke hos Rentekutt).</response>
    [HttpPost("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status([FromBody] StatusCallbackDto melding)
    {
        var bank = await AutentisertBankAsync();
        if (bank is null) return Unauthorized(new { error = "Mangler/ugyldig token." });
        if (melding is null || string.IsNullOrWhiteSpace(melding.HendelseId) || string.IsNullOrWhiteSpace(melding.Referanse))
            return BadRequest(new { error = "Mangler hendelseId, referanse eller status." });

        // Referansen må være en kjent søknad hos oss (= Rentekutt sin sak-id).
        if (!Guid.TryParse(melding.Referanse, out var sakId) || await _kundekort.GetAsync(sakId) is not { } sak)
            return NotFound(new { error = "Ukjent referanse." });
        // ...og den må være sendt til DIN bank.
        if (!string.Equals(sak.DelegertBank, bank, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Søknaden tilhører en annen bank." });

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

    /// <summary>Banken i et gyldig Bearer-token, eller null hvis token mangler/er ugyldig/utløpt.</summary>
    private async Task<string?> AutentisertBankAsync()
    {
        var secret = await _kreds.SecretAsync();
        if (string.IsNullOrWhiteSpace(secret)) return null;
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return BankFraToken(auth["Bearer ".Length..].Trim(), secret);
    }

    private static string LagToken(string sub, string bank, string secret, int levetidSek)
    {
        var naa = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "rentekutt", ["sub"] = sub, ["bank"] = bank, ["iat"] = naa, ["exp"] = naa + levetidSek,
        });
        var payload = B64Url(Encoding.UTF8.GetBytes(payloadJson));
        var sig = B64Url(Hmac(secret, $"{header}.{payload}"));
        return $"{header}.{payload}.{sig}";
    }

    /// <summary>Validerer signatur + utløp og returnerer «bank»-kravet, ellers null.</summary>
    private static string? BankFraToken(string token, string secret)
    {
        var deler = token.Split('.');
        if (deler.Length != 3) return null;
        var forventet = B64Url(Hmac(secret, $"{deler[0]}.{deler[1]}"));
        if (!PartnerKredsService.KonstantLik(forventet, deler[2])) return null;
        try
        {
            var json = Encoding.UTF8.GetString(FromB64Url(deler[1]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("exp", out var exp) || exp.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return root.TryGetProperty("bank", out var b) ? b.GetString() : null;
        }
        catch { return null; }
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

    // ---- Mapping Kundekort → SoknadDto (for GET /soknader) ----

    private static SoknadDto MapTilSoknad(Kundekort k) => new()
    {
        Referanse = k.Id.ToString(),
        Kundetype = k.KundeType == "B2B" ? Kundetype.Bedrift : Kundetype.Privat,
        Laanetype = MapLaanetype(k.Laanetype),
        OnsketBelop = (long)(k.OnsketLaanebelop ?? 0),
        NedbetalingstidMnd = k.OnsketLopetidMnd,
        RefinansieresBelop = k.RefinansieresBelop is > 0 ? (long)k.RefinansieresBelop.Value : null,
        Opprettet = k.CreatedAt,
        Samtykke = new SamtykkeDto
        {
            SamtykkeId = k.Id.ToString(),
            Tidspunkt = k.CreatedAt,
            VilkaarVersjon = k.SamtykkeGjeldsregisterKredittsjekk ? "samtykket" : "ukjent",
        },
        Soker = new SokerDto
        {
            FulltNavn = k.FulltNavn ?? "",
            Fodselsnummer = k.Foedselsnummer ?? "",
            Epost = k.Epost ?? "",
            Mobilnummer = k.Mobilnummer ?? "",
            Adresse = k.Adresse,
            Postnummer = k.Postnummer,
            Poststed = k.Poststed,
            AarsinntektBrutto = k.AarsinntektBrutto is > 0 ? (long)k.AarsinntektBrutto.Value : null,
            UsikretGjeld = null,
            Boliggjeld = k.Boliggjeld is > 0 ? (long)k.Boliggjeld.Value : null,
        },
        Firma = k.KundeType == "B2B" ? new FirmaDto
        {
            Firmanavn = k.FulltNavn ?? "",
            Organisasjonsnummer = new string((k.Orgnr ?? k.KundeId ?? "").Where(char.IsDigit).ToArray()),
        } : null,
    };

    private static Laanetype MapLaanetype(string? s)
    {
        var x = (s ?? "").ToLowerInvariant();
        if (x.Contains("bolig")) return Laanetype.Boliglan;
        if (x.Contains("kredittkort")) return Laanetype.Kredittkort;
        if (x.Contains("bedrift")) return Laanetype.Bedriftslan;
        if (x.Contains("refinans")) return Laanetype.Refinansiering;
        return Laanetype.Forbrukslan;
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
