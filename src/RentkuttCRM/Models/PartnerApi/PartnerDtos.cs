using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace RentkuttCRM.Models.PartnerApi;

/// <summary>
/// En låne-/refinansieringssøknad som Rentekutt sender til bankpartneren. Alle beløp er i NOK.
/// Feltene uten verdi utelates. Bruk <see cref="Referanse"/> i status-tilbakemeldingen tilbake til oss.
/// </summary>
public class SoknadDto
{
    /// <summary>Unik referanse for søknaden hos Rentekutt (UUID). Oppgis i status-callback.</summary>
    [Required]
    public string Referanse { get; set; } = "";

    /// <summary>Kundetype: «privat» (B2C) eller «bedrift» (B2B).</summary>
    [Required]
    public string Kundetype { get; set; } = "privat";

    /// <summary>Produkt/lånetype, f.eks. «Forbrukslån», «Refinansiering», «Boliglån», «Kredittkort», «Bedriftslån».</summary>
    [Required]
    public string Laanetype { get; set; } = "";

    /// <summary>Ønsket lånebeløp / kredittramme i NOK.</summary>
    [Required]
    public decimal OnsketBelop { get; set; }

    /// <summary>Ønsket nedbetalingstid i måneder (for lån; utelates for kredittkort/-ramme).</summary>
    public int? NedbetalingstidMnd { get; set; }

    /// <summary>Beløp som skal refinansieres i NOK, hvis søknaden gjelder refinansiering.</summary>
    public decimal? RefinansieresBelop { get; set; }

    /// <summary>Hovedsøker.</summary>
    [Required]
    public SokerDto Soker { get; set; } = new();

    /// <summary>Medsøker, hvis søknaden har to søkere.</summary>
    public SokerDto? Medsoker { get; set; }

    /// <summary>Eiendom/sikkerhet — kun ved boliglån.</summary>
    public EiendomDto? Eiendom { get; set; }

    /// <summary>Firmaopplysninger — kun for bedriftssøknader (B2B).</summary>
    public FirmaDto? Firma { get; set; }

    /// <summary>Tidspunkt søknaden ble opprettet (ISO-8601, UTC).</summary>
    public DateTime Opprettet { get; set; }
}

/// <summary>Person (søker eller medsøker) med personalia og økonomi.</summary>
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

    /// <summary>Brutto årsinntekt i NOK.</summary>
    public decimal? AarsinntektBrutto { get; set; }

    /// <summary>Sivilstatus, f.eks. «Gift», «Enslig», «Samboer».</summary>
    public string? Sivilstatus { get; set; }

    /// <summary>Arbeidssituasjon, f.eks. «Fast ansatt», «Selvstendig», «Pensjonist».</summary>
    public string? Arbeidssituasjon { get; set; }

    /// <summary>Boforhold, f.eks. «Eier», «Leier».</summary>
    public string? Boforhold { get; set; }

    public int? AntallBarnUnder18 { get; set; }

    /// <summary>Samlet usikret gjeld (forbrukslån/kredittkort) i NOK.</summary>
    public decimal? UsikretGjeld { get; set; }

    /// <summary>Boliggjeld i NOK.</summary>
    public decimal? Boliggjeld { get; set; }

    public decimal? Studielaan { get; set; }
    public decimal? Billaan { get; set; }
}

/// <summary>Eiendom/sikkerhetsobjekt ved boliglån (matrikkel).</summary>
public class EiendomDto
{
    public string? Kommune { get; set; }
    public int? Kommunenummer { get; set; }
    public int? Gaardsnummer { get; set; }
    public int? Bruksnummer { get; set; }
    public int? Seksjonsnummer { get; set; }

    /// <summary>Estimert markedsverdi i NOK.</summary>
    public decimal? EstimertVerdi { get; set; }

    /// <summary>Fellesgjeld (for andel/borettslag) i NOK.</summary>
    public decimal? Fellesgjeld { get; set; }

    /// <summary>Organisasjonsnummer for borettslag/sameie, hvis relevant.</summary>
    public string? BorettslagOrgnr { get; set; }
}

/// <summary>Firmaopplysninger for bedriftssøknader.</summary>
public class FirmaDto
{
    [Required] public string Firmanavn { get; set; } = "";

    /// <summary>Organisasjonsnummer, 9 siffer.</summary>
    [Required] public string Organisasjonsnummer { get; set; } = "";

    /// <summary>Estimert omsetning inneværende år i NOK.</summary>
    public decimal? OmsetningIAar { get; set; }

    /// <summary>Låneformål, f.eks. «Investering», «Likviditet», «Annet».</summary>
    public string? Laaneformaal { get; set; }
}

/// <summary>
/// Status-tilbakemelding fra banken på en søknad. Sendes som POST til Rentekutt sitt
/// status-endepunkt med Bearer-token. Idempotent — samme status kan sendes flere ganger.
/// </summary>
public class StatusCallbackDto
{
    /// <summary>Referansen fra søknaden (<see cref="SoknadDto.Referanse"/>).</summary>
    [Required] public string Referanse { get; set; } = "";

    /// <summary>
    /// Ny status for søknaden. Gyldige verdier:
    /// «mottatt», «under_behandling», «innvilget», «utbetalt», «avslatt», «kansellert».
    /// </summary>
    [Required] public string Status { get; set; } = "";

    /// <summary>Innvilget beløp i NOK (ved «innvilget»/«utbetalt»).</summary>
    public decimal? InnvilgetBelop { get; set; }

    /// <summary>Signeringslenke kunden kan bruke for å signere, hvis relevant.</summary>
    public string? Signeringslenke { get; set; }

    /// <summary>Begrunnelse ved «avslatt».</summary>
    public string? Begrunnelse { get; set; }

    /// <summary>Bankens egen saks-/referanse-id.</summary>
    public string? BankReferanse { get; set; }
}
