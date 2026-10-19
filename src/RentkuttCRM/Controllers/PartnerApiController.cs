using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentkuttCRM.Models.PartnerApi;
using RentkuttCRM.Services;

namespace RentkuttCRM.Controllers;

/// <summary>
/// Rentekutt Partner API — for bankpartnere som mottar søknader fra Rentekutt.
///
/// Flyt: Rentekutt sender en søknad (<see cref="SoknadDto"/>) til bankens mottaks-endepunkt.
/// Banken behandler den og sender statusendringer tilbake til Rentekutt via
/// <c>POST /api/partner/status</c> (Bearer-token). Alle beløp er i NOK.
/// </summary>
[ApiController]
[Route("api/partner")]
[ApiExplorerSettings(GroupName = "partner")]
[Produces("application/json")]
public class PartnerApiController : ControllerBase
{
    /// <summary>Innstillingsnøkkel for Bearer-token som status-callbacks autentiseres med.</summary>
    public const string KeyToken = "partner_api_token";

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
    /// Eksempel på en søknad slik Rentekutt sender den. Bruk responsen som mal for å bygge
    /// bankens mottaks-endepunkt. Krever ingen autentisering (kun et skjema-/eksempelkall).
    /// </summary>
    /// <returns>Et ferdig utfylt eksempel på <see cref="SoknadDto"/>.</returns>
    [HttpGet("soknad/eksempel")]
    [ProducesResponseType(typeof(SoknadDto), StatusCodes.Status200OK)]
    public ActionResult<SoknadDto> SoknadEksempel() => Ok(Eksempel());

    /// <summary>
    /// Bankens status-tilbakemelding på en søknad. Autentiseres med Bearer-token
    /// (<c>Authorization: Bearer &lt;token&gt;</c>) som Rentekutt utleverer til partneren.
    /// </summary>
    /// <param name="melding">Statusoppdatering knyttet til søknadens referanse.</param>
    /// <response code="200">Statusen er mottatt og registrert.</response>
    /// <response code="400">Mangler referanse eller status.</response>
    /// <response code="401">Ugyldig eller manglende Bearer-token.</response>
    [HttpPost("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Status([FromBody] StatusCallbackDto melding)
    {
        var forventet = await _settings.GetAsync(KeyToken);
        var presentert = BearerToken();
        if (string.IsNullOrWhiteSpace(forventet))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Partner-API er ikke aktivert (mangler token)." });
        if (!KonstantLik(forventet, presentert))
            return Unauthorized(new { error = "Ugyldig token." });

        if (melding is null || string.IsNullOrWhiteSpace(melding.Referanse) || string.IsNullOrWhiteSpace(melding.Status))
            return BadRequest(new { error = "Mangler referanse eller status." });

        // Logg statusen på saken hvis referansen er en kjent sak (referanse = Rentekutt sin søknads-id).
        if (Guid.TryParse(melding.Referanse, out var sakId) && await _kundekort.GetAsync(sakId) is not null)
        {
            var detalj = $"Partner-status: {melding.Status}"
                + (melding.InnvilgetBelop is { } b ? $" · innvilget {b:N0} kr" : "")
                + (string.IsNullOrWhiteSpace(melding.Begrunnelse) ? "" : $" · {melding.Begrunnelse}")
                + (string.IsNullOrWhiteSpace(melding.BankReferanse) ? "" : $" · bankref {melding.BankReferanse}");
            try { await _logg.LoggAsync(sakId, "Bankpartner", detalj, "bank"); }
            catch (Exception ex) { _log.LogWarning(ex, "Logging av partner-status feilet"); }
        }

        _log.LogInformation("Partner-status mottatt: ref={Ref} status={Status}", melding.Referanse, melding.Status);
        return Ok(new { ok = true, referanse = melding.Referanse, status = melding.Status });
    }

    // ---- Hjelpere ----

    private string? BearerToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
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
        Kundetype = "privat",
        Laanetype = "Refinansiering",
        OnsketBelop = 350000,
        NedbetalingstidMnd = 60,
        RefinansieresBelop = 280000,
        Opprettet = new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc),
        Soker = new SokerDto
        {
            FulltNavn = "Ola Nordmann",
            Fodselsnummer = "15057243306",
            Epost = "ola.nordmann@example.com",
            Mobilnummer = "40412345",
            Adresse = "Storgata 1",
            Postnummer = "0155",
            Poststed = "Oslo",
            AarsinntektBrutto = 640000,
            Sivilstatus = "Gift",
            Arbeidssituasjon = "Fast ansatt",
            Boforhold = "Eier",
            AntallBarnUnder18 = 2,
            UsikretGjeld = 280000,
            Boliggjeld = 2400000,
            Studielaan = 120000,
        },
        Eiendom = null,
    };
}
