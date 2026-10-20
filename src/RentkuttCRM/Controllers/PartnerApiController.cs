using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RentkuttCRM.Models.PartnerApi;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Endepunktene for bankpartnere.
/// </summary>
[ApiController]
[Route("api/partner/v1")]
[ApiExplorerSettings(GroupName = "partner")]
[Tags("Endepunkt usikret gjeld")]
[Produces("application/json")]
[Consumes("application/json")]
[EnableRateLimiting("partner")]
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
    [Tags("Endepunkt login og status")]
    [EnableRateLimiting("partner-login")]
    [ProducesResponseType(typeof(TokenSvar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Token([FromBody] PartnerLoginDto paalogging)
    {
        if ((await _kreds.HentAsync()).Count == 0)
            return Feil(StatusCodes.Status503ServiceUnavailable, "Ikke aktivert", "Partner-API er ikke aktivert.");

        var kreds = await _kreds.ValiderAsync(paalogging?.Username, paalogging?.Password);
        if (kreds is null) return Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Feil brukernavn eller passord.");

        var secret = await _kreds.SecretAsync();
        var token = LagToken(kreds.Username, kreds.Bank, secret, TokenLevetidSek);
        return Ok(new TokenSvar { AccessToken = token, TokenType = "Bearer", ExpiresIn = TokenLevetidSek });
    }

    /// <summary>
    /// Hent ÉN refinansierings-/forbrukslånssøknad på referanse (UUID). Kun søknader sendt til din bank.
    /// </summary>
    /// <param name="referanse">Søknadens unike referanse (UUID) fra Rentekutt.</param>
    /// <response code="200">Søknaden.</response>
    /// <response code="401">Mangler eller ugyldig token.</response>
    /// <response code="403">Søknaden tilhører en annen bank.</response>
    /// <response code="404">Ingen refinansieringssøknad med denne referansen.</response>
    [HttpGet("refinansiering/{referanse:guid}")]
    [ProducesResponseType(typeof(SoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Refinansiering(Guid referanse)
    {
        var bank = await AutentisertBankAsync();
        if (bank is null) return Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Mangler/ugyldig token.");
        var k = await _kundekort.GetAsync(referanse);
        if (k is null || ErBoliglaan(k.Laanetype))
            return Feil(StatusCodes.Status404NotFound, "Ikke funnet", "Ingen refinansieringssøknad med denne referansen.");
        if (!string.Equals(k.DelegertBank, bank, StringComparison.OrdinalIgnoreCase))
            return Feil(StatusCodes.Status403Forbidden, "Ingen tilgang", "Søknaden tilhører en annen bank.");
        return Ok(MapTilSoknad(k));
    }

    /// <summary>Eksempel på en refinansierings-/forbrukslånssøknad. Krever token.</summary>
    [HttpGet("refinansiering/eksempel")]
    [ProducesResponseType(typeof(SoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefinansieringEksempel()
        => await AutentisertBankAsync() is not null ? Ok(Eksempel()) : Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Mangler/ugyldig token.");

    /// <summary>
    /// Hent ÉN boliglånssøknad på referanse (UUID). Kun søknader sendt til din bank.
    /// </summary>
    /// <param name="referanse">Søknadens unike referanse (UUID) fra Rentekutt.</param>
    /// <response code="200">Søknaden.</response>
    /// <response code="401">Mangler eller ugyldig token.</response>
    /// <response code="403">Søknaden tilhører en annen bank.</response>
    /// <response code="404">Ingen boliglånssøknad med denne referansen.</response>
    [HttpGet("boliglan/{referanse:guid}")]
    [Tags("Endepunkt boliglån")]
    [ProducesResponseType(typeof(BoliglanSoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Boliglan(Guid referanse)
    {
        var bank = await AutentisertBankAsync();
        if (bank is null) return Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Mangler/ugyldig token.");
        var k = await _kundekort.GetAsync(referanse);
        if (k is null || !ErBoliglaan(k.Laanetype))
            return Feil(StatusCodes.Status404NotFound, "Ikke funnet", "Ingen boliglånssøknad med denne referansen.");
        if (!string.Equals(k.DelegertBank, bank, StringComparison.OrdinalIgnoreCase))
            return Feil(StatusCodes.Status403Forbidden, "Ingen tilgang", "Søknaden tilhører en annen bank.");
        return Ok(MapTilBoliglan(k));
    }

    /// <summary>Eksempel på en boliglånssøknad (mindre skjema). Krever token.</summary>
    [HttpGet("boliglan/eksempel")]
    [Tags("Endepunkt boliglån")]
    [ProducesResponseType(typeof(BoliglanSoknadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BoliglanEksempel()
        => await AutentisertBankAsync() is not null ? Ok(BoliglanEksempelData()) : Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Mangler/ugyldig token.");

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
    [Tags("Endepunkt login og status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status([FromBody] StatusCallbackDto melding)
    {
        var bank = await AutentisertBankAsync();
        if (bank is null) return Feil(StatusCodes.Status401Unauthorized, "Ikke autorisert", "Mangler/ugyldig token.");
        if (melding is null || string.IsNullOrWhiteSpace(melding.HendelseId) || string.IsNullOrWhiteSpace(melding.Referanse))
            return Feil(StatusCodes.Status400BadRequest, "Ugyldig forespørsel", "Mangler hendelseId, referanse eller status.");

        // Referansen må være en kjent søknad hos oss (= Rentekutt sin sak-id).
        if (!Guid.TryParse(melding.Referanse, out var sakId) || await _kundekort.GetAsync(sakId) is not { } sak)
            return Feil(StatusCodes.Status404NotFound, "Ikke funnet", "Ukjent referanse.");
        // ...og den må være sendt til DIN bank.
        if (!string.Equals(sak.DelegertBank, bank, StringComparison.OrdinalIgnoreCase))
            return Feil(StatusCodes.Status403Forbidden, "Ingen tilgang", "Søknaden tilhører en annen bank.");

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

    // ---- Feilsvar i standardformat (RFC 7807 ProblemDetails) ----

    private ObjectResult Feil(int status, string tittel, string detalj)
        => Problem(statusCode: status, title: tittel, detail: detalj);

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

    private static bool ErBoliglaan(string? laanetype) => (laanetype ?? "").Contains("bolig", StringComparison.OrdinalIgnoreCase);

    // ---- Felt-hjelpere: beløp → hele kroner, tomt → null, fritekst/kode → enum ----

    private static long? Kr(decimal? v) => v is > 0 ? (long)Math.Round(v.Value) : null;
    private static string? Tekst(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Kundekortet lagrer fritekst (fra KundekortService-listene) + ev. en kode fra skjemaet.
    // Vi matcher på begge, fritekst først. Ukjent → null (feltet utelates), aldri gjetting.
    private static string Norm(string? tekst, string? kode) => ((tekst ?? "") + " " + (kode ?? "")).ToLowerInvariant();

    private static Sivilstatus? MapSivilstatus(string? tekst, string? kode)
    {
        var s = Norm(tekst, kode);
        if (s.Contains("gift")) return Sivilstatus.Gift;
        if (s.Contains("samboer")) return Sivilstatus.Samboer;
        if (s.Contains("skilt") || s.Contains("separert")) return Sivilstatus.Skilt;
        if (s.Contains("enke")) return Sivilstatus.Enke;
        if (s.Contains("singel") || s.Contains("enslig") || s.Contains("ugift")) return Sivilstatus.Enslig;
        return null;
    }

    private static Arbeidssituasjon? MapArbeid(string? tekst, string? kode)
    {
        var s = Norm(tekst, kode);
        if (s.Contains("midlertidig") || s.Contains("vikar")) return Arbeidssituasjon.MidlertidigAnsatt;
        if (s.Contains("fast") || s.Contains("offentlig")) return Arbeidssituasjon.FastAnsatt;
        if (s.Contains("selvstendig") || s.Contains("næring")) return Arbeidssituasjon.Selvstendig;
        if (s.Contains("pensjon")) return Arbeidssituasjon.Pensjonist;
        if (s.Contains("ufør") || s.Contains("trygd")) return Arbeidssituasjon.Trygd;
        if (s.Contains("student")) return Arbeidssituasjon.Student;
        if (s.Contains("arbeidsledig") || s.Contains("hjemme")) return Arbeidssituasjon.Arbeidsledig;
        return null;
    }

    private static Boforhold? MapBoforhold(string? tekst, string? kode)
    {
        var s = Norm(tekst, kode);
        if (s.Contains("foreldre")) return Boforhold.BorHosForeldre;
        if (s.Contains("leier")) return Boforhold.Leier;
        if (s.Contains("eier") || s.Contains("andel") || s.Contains("borettslag") || s.Contains("enebolig")) return Boforhold.Eier;
        return null;
    }

    // ---- Mapping Kundekort → Boliglån (mindre skjema) ----

    private static BoliglanSoknadDto MapTilBoliglan(Kundekort k) => new()
    {
        Referanse = k.Id.ToString(),
        OnsketBelop = (long)(k.OnsketLaanebelop ?? 0),
        NedbetalingstidMnd = k.OnsketLopetidMnd,
        Opprettet = k.CreatedAt,
        Samtykke = new SamtykkeDto
        {
            SamtykkeId = k.Id.ToString(),
            Tidspunkt = k.CreatedAt,
            VilkaarVersjon = k.SamtykkeGjeldsregisterKredittsjekk ? "samtykket" : "ukjent",
        },
        Soker = new BoliglanSokerDto
        {
            FulltNavn = k.FulltNavn ?? "",
            Fodselsnummer = k.Foedselsnummer ?? "",
            Epost = k.Epost ?? "",
            Mobilnummer = k.Mobilnummer ?? "",
            Adresse = k.Adresse,
            Postnummer = k.Postnummer,
            Poststed = k.Poststed,
            AarsinntektBrutto = Kr(k.AarsinntektBrutto),
            Arbeidssituasjon = MapArbeid(k.Arbeidssituasjon, k.ArbeidssituasjonKode),
        },
        Medsoker = !k.HarMedsoker ? null : new BoliglanSokerDto
        {
            FulltNavn = k.MedsokerNavn ?? "",
            Fodselsnummer = k.MedsokerFoedselsnummer ?? "",
            Epost = k.MedsokerEpost ?? "",
            Mobilnummer = k.MedsokerMobil ?? "",
            Adresse = k.MedsokerAdresse,
            Postnummer = k.MedsokerPostnummer,
            Poststed = k.MedsokerPoststed,
            AarsinntektBrutto = Kr(k.MedsokerInntekt),
            Arbeidssituasjon = MapArbeid(k.MedsokerArbeidsforhold, k.MedsokerArbeidssituasjonKode),
        },
        Bolig = new BoligDto
        {
            Boligverdi = k.Boligverdi is > 0 ? (long)k.Boligverdi.Value : null,
            BelaaningsgradProsent = k.Belaaningsgrad,
            NaavaerendeBoliglaan = k.NaavaerendeLaanebelop is > 0 ? (long)k.NaavaerendeLaanebelop.Value : null,
            Boliggjeld = k.Boliggjeld is > 0 ? (long)k.Boliggjeld.Value : null,
            NaavaerendeRente = k.NaavaerendeRente,
            NaavaerendeBank = k.NavarendeBank,
        },
    };

    private static BoliglanSoknadDto BoliglanEksempelData() => new()
    {
        Referanse = "7a2b9c10-3d4e-4f56-8a90-0b1c2d3e4f50",
        OnsketBelop = 3500000,
        NedbetalingstidMnd = 300,
        Opprettet = new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc),
        Samtykke = new SamtykkeDto
        {
            SamtykkeId = "9d2f6a31-0b4c-4d8e-9f31-2b3c4d5e6f71",
            Tidspunkt = new DateTime(2026, 10, 19, 9, 28, 0, DateTimeKind.Utc),
            VilkaarVersjon = "2026-01",
        },
        Soker = new BoliglanSokerDto
        {
            FulltNavn = "Ola Nordmann",
            Fodselsnummer = "15857243371",
            Epost = "ola.nordmann@example.com",
            Mobilnummer = "40412345",
            Adresse = "Storgata 1",
            Postnummer = "0155",
            Poststed = "Oslo",
            AarsinntektBrutto = 740000,
            Arbeidssituasjon = Arbeidssituasjon.FastAnsatt,
        },
        Bolig = new BoligDto
        {
            Boligverdi = 5200000,
            BelaaningsgradProsent = 67.3m,
            NaavaerendeBoliglaan = 3200000,
            Boliggjeld = 3200000,
            NaavaerendeRente = 5.9m,
            NaavaerendeBank = "DNB",
        },
    };

    // ---- Mapping Kundekort → SoknadDto (refinansiering/forbrukslån) ----

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
            AarsinntektBrutto = Kr(k.AarsinntektBrutto),
            Sivilstatus = MapSivilstatus(k.Sivilstatus, k.SivilstatusKode),
            Arbeidssituasjon = MapArbeid(k.Arbeidssituasjon, k.ArbeidssituasjonKode),
            Boforhold = MapBoforhold(k.Boforhold, k.BoforholdKode),
            AntallBarnUnder18 = k.AntallBarnUnder18,
            UsikretGjeld = Kr(k.Forbruksgjeld),
            Boliggjeld = Kr(k.Boliggjeld),
            Studielaan = Kr(k.Studielaan),
            Billaan = Kr(k.Billaan),
            Arbeidsgiver = Tekst(k.Arbeidsgiver),
            BoutgifterMnd = Kr(k.BoligkostnadMnd),
            Statsborgerskap = Tekst(k.StatsborgerskapKode) ?? Tekst(k.Statsborgerskap),
            BotidAar = k.BotidMnd is > 0 ? k.BotidMnd / 12 : null,
        },
        Medsoker = !k.HarMedsoker ? null : new SokerDto
        {
            FulltNavn = k.MedsokerNavn ?? "",
            Fodselsnummer = k.MedsokerFoedselsnummer ?? "",
            Epost = k.MedsokerEpost ?? "",
            Mobilnummer = k.MedsokerMobil ?? "",
            Adresse = k.MedsokerAdresse,
            Postnummer = k.MedsokerPostnummer,
            Poststed = k.MedsokerPoststed,
            AarsinntektBrutto = Kr(k.MedsokerInntekt),
            Arbeidssituasjon = MapArbeid(k.MedsokerArbeidsforhold, k.MedsokerArbeidssituasjonKode),
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
