using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RentkuttCRM.Models.PartnerApi;

// ===== Faste verdier (enum) — unngår fritekst-varianter =====

public enum Kundetype { Privat, Bedrift }

public enum Laanetype { Forbrukslan, Refinansiering, Boliglan, Kredittkort, Bedriftslan }

public enum Sivilstatus { Gift, Samboer, Enslig, Skilt, Enke }

public enum Arbeidssituasjon { FastAnsatt, MidlertidigAnsatt, Selvstendig, Pensjonist, Trygd, Student, Arbeidsledig }

public enum Boforhold { Eier, Leier, BorHosForeldre }

/// <summary>Statusene banken kan melde tilbake, og lovlige overganger (forenklet):
/// mottatt → under_behandling → (trenger_dokumentasjon →) tilbud → innvilget → utbetalt.
/// avslatt/kansellert kan nås fra de fleste tilstander.</summary>
public enum PartnerStatus { Mottatt, UnderBehandling, TrengerDokumentasjon, Tilbud, Innvilget, Utbetalt, Avslatt, Kansellert }

// ===== Innlogging =====

/// <summary>Innlogging mot Partner API. Brukernavn og passord utleveres av Rentekutt.</summary>
public class PartnerLoginDto
{
    [Required] public string Username { get; set; } = "";
    [Required] public string Password { get; set; } = "";
}

/// <summary>Svar fra innloggingen — en tidsbestemt token (Bearer).</summary>
public class TokenSvar
{
    /// <summary>Selve tokenet. Sendes som «Authorization: Bearer &lt;access_token&gt;».</summary>
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    /// <summary>Alltid «Bearer».</summary>
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "Bearer";
    /// <summary>Gyldighet i sekunder (3600 = 1 time).</summary>
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}

// ===== Søknad (Rentekutt → bank) =====

/// <summary>
/// En låne-/refinansieringssøknad som Rentekutt sender til bankpartneren. Alle beløp er i hele
/// norske kroner (heltall). Felt uten verdi utelates. Bruk <see cref="Referanse"/> i statusmeldingene.
/// </summary>
public class SoknadDto
{
    /// <summary>Unik referanse for søknaden hos Rentekutt (UUID). Oppgis i alle statusmeldinger.</summary>
    [Required] public string Referanse { get; set; } = "";

    /// <summary>Kundetype.</summary>
    [Required] public Kundetype Kundetype { get; set; }

    /// <summary>Produkt/lånetype.</summary>
    [Required] public Laanetype Laanetype { get; set; }

    /// <summary>Ønsket lånebeløp / kredittramme i hele kroner.</summary>
    [Required] public long OnsketBelop { get; set; }

    /// <summary>Ønsket nedbetalingstid i måneder (for lån; utelates for kredittkort/-ramme).</summary>
    public int? NedbetalingstidMnd { get; set; }

    /// <summary>Beløp som skal refinansieres (hele kroner), hvis relevant.</summary>
    public long? RefinansieresBelop { get; set; }

    /// <summary>Kundens samtykke til at dataene deles med denne banken (påkrevd av personvern).</summary>
    [Required] public SamtykkeDto Samtykke { get; set; } = new();

    /// <summary>Hovedsøker.</summary>
    [Required] public SokerDto Soker { get; set; } = new();

    /// <summary>Medsøker, hvis søknaden har to søkere.</summary>
    public SokerDto? Medsoker { get; set; }

    /// <summary>Eiendom/sikkerhet — kun ved boliglån.</summary>
    public EiendomDto? Eiendom { get; set; }

    /// <summary>Firmaopplysninger — kun for bedriftssøknader.</summary>
    public FirmaDto? Firma { get; set; }

    /// <summary>Tidspunkt søknaden ble opprettet (ISO-8601, UTC).</summary>
    public DateTime Opprettet { get; set; }
}

/// <summary>Kundens samtykke til deling av data med banken.</summary>
public class SamtykkeDto
{
    /// <summary>Unik id for samtykket (sporbarhet).</summary>
    [Required] public string SamtykkeId { get; set; } = "";
    /// <summary>Tidspunkt samtykket ble gitt (ISO-8601, UTC).</summary>
    [Required] public DateTime Tidspunkt { get; set; }
    /// <summary>Versjon av vilkårene kunden samtykket til.</summary>
    public string? VilkaarVersjon { get; set; }
}

/// <summary>Person (søker eller medsøker) med personalia og økonomi. Beløp i hele kroner.</summary>
public class SokerDto
{
    [Required] public string FulltNavn { get; set; } = "";

    /// <summary>Norsk fødselsnummer, 11 siffer.</summary>
    [Required] public string Fodselsnummer { get; set; } = "";

    [Required] public string Epost { get; set; } = "";
    [Required] public string Mobilnummer { get; set; } = "";
    public string? Adresse { get; set; }
    public string? Postnummer { get; set; }
    public string? Poststed { get; set; }

    /// <summary>Brutto årsinntekt i hele kroner.</summary>
    public long? AarsinntektBrutto { get; set; }

    public Sivilstatus? Sivilstatus { get; set; }
    public Arbeidssituasjon? Arbeidssituasjon { get; set; }
    public Boforhold? Boforhold { get; set; }
    public int? AntallBarnUnder18 { get; set; }

    /// <summary>Samlet usikret gjeld (forbrukslån/kredittkort) i hele kroner.</summary>
    public long? UsikretGjeld { get; set; }
    public long? Boliggjeld { get; set; }
    public long? Studielaan { get; set; }
    public long? Billaan { get; set; }

    // --- Felt mange banker krever (valgfrie — bekreft mot den enkelte bankens krav) ---
    public string? Arbeidsgiver { get; set; }
    /// <summary>Ansatt siden (ISO-8601 dato).</summary>
    public DateTime? AnsattSiden { get; set; }
    /// <summary>Månedlige boutgifter i hele kroner (husleie/felleskost).</summary>
    public long? BoutgifterMnd { get; set; }
    /// <summary>Statsborgerskap (ISO 3166-1 alpha-2, f.eks. «NO»).</summary>
    public string? Statsborgerskap { get; set; }
    /// <summary>Botid i Norge i år.</summary>
    public int? BotidAar { get; set; }
    /// <summary>Politisk eksponert person (PEP).</summary>
    public bool? Pep { get; set; }
}

/// <summary>Eiendom/sikkerhetsobjekt ved boliglån (matrikkel). Beløp i hele kroner.</summary>
public class EiendomDto
{
    public string? Kommune { get; set; }
    public int? Kommunenummer { get; set; }
    public int? Gaardsnummer { get; set; }
    public int? Bruksnummer { get; set; }
    public int? Seksjonsnummer { get; set; }
    public long? EstimertVerdi { get; set; }
    public long? Fellesgjeld { get; set; }
    /// <summary>Organisasjonsnummer for borettslag/sameie, hvis relevant.</summary>
    public string? BorettslagOrgnr { get; set; }
}

/// <summary>Firmaopplysninger for bedriftssøknader. Beløp i hele kroner.</summary>
public class FirmaDto
{
    [Required] public string Firmanavn { get; set; } = "";
    /// <summary>Organisasjonsnummer, 9 siffer.</summary>
    [Required] public string Organisasjonsnummer { get; set; } = "";
    public long? OmsetningIAar { get; set; }
    public string? Laaneformaal { get; set; }
}

// ===== Status (bank → Rentekutt) =====

/// <summary>
/// Status-tilbakemelding fra banken på en søknad. Sendes som POST med Bearer-token.
/// Hver melding har en unik <see cref="HendelseId"/> (idempotens — samme melding registreres kun én gang).
/// </summary>
public class StatusCallbackDto
{
    /// <summary>Unik id for denne hendelsen (idempotensnøkkel). Samme id ignoreres ved gjentakelse.</summary>
    [Required] public string HendelseId { get; set; } = "";

    /// <summary>Referansen fra søknaden (<see cref="SoknadDto.Referanse"/>).</summary>
    [Required] public string Referanse { get; set; } = "";

    /// <summary>Ny status for søknaden.</summary>
    [Required] public PartnerStatus Status { get; set; }

    /// <summary>Tidspunkt for hendelsen (ISO-8601, UTC).</summary>
    [Required] public DateTime Tidspunkt { get; set; }

    /// <summary>Lånetilbud — påkrevd ved status «Tilbud» (og gjerne «Innvilget»). Grunnlag for sammenligning.</summary>
    public TilbudDto? Tilbud { get; set; }

    /// <summary>Innvilget beløp i hele kroner (ved «Innvilget»/«Utbetalt»).</summary>
    public long? InnvilgetBelop { get; set; }

    /// <summary>Utbetalt beløp i hele kroner (ved «Utbetalt»).</summary>
    public long? UtbetaltBelop { get; set; }

    /// <summary>Utbetalingsdato (ved «Utbetalt»).</summary>
    public DateTime? UtbetaltDato { get; set; }

    /// <summary>Signeringslenke kunden kan bruke, hvis relevant.</summary>
    public string? Signeringslenke { get; set; }

    /// <summary>Begrunnelse ved «Avslatt», eller hvilken dokumentasjon som mangler ved «TrengerDokumentasjon».</summary>
    public string? Begrunnelse { get; set; }

    /// <summary>Bankens egen saks-/referanse-id.</summary>
    public string? BankReferanse { get; set; }
}

/// <summary>Et konkret lånetilbud fra banken. Renter i prosent, beløp i hele kroner.</summary>
public class TilbudDto
{
    /// <summary>Nominell rente i prosent (f.eks. 9.90).</summary>
    public decimal? NominellRente { get; set; }
    /// <summary>Effektiv rente i prosent (f.eks. 11.30).</summary>
    public decimal? EffektivRente { get; set; }
    public long? Etableringsgebyr { get; set; }
    public long? Termingebyr { get; set; }
    /// <summary>Månedsbeløp i hele kroner.</summary>
    public long? MaanedsbelopKr { get; set; }
    public int? NedbetalingstidMnd { get; set; }
    /// <summary>Totalkostnad over løpetiden i hele kroner.</summary>
    public long? TotalkostnadKr { get; set; }
    /// <summary>Hvor lenge tilbudet gjelder (ISO-8601).</summary>
    public DateTime? GyldigTil { get; set; }
}
