using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>
/// Kundekort = lånesøknad med lånedata. Mappes mot tabellen kundekort.
/// Identifikator: fødselsnummer (11) for B2C / orgnr (9) for B2B.
/// </summary>
[Table("kundekort")]
public class Kundekort : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("kunde_id")] public string KundeId { get; set; } = "";
    [Column("kunde_type")] public string KundeType { get; set; } = "B2C";

    /// <summary>Organisasjonsnummer for B2B. Mappes fra payload-feltet «orgnr».
    /// kunde_id speiler dette for gruppering, men «Orgnr» i UI leser herfra.</summary>
    [Column("orgnr")] public string? Orgnr { get; set; }

    // A. Søker
    [Column("fullt_navn")] public string? FulltNavn { get; set; }
    [Column("foedselsnummer")] public string? Foedselsnummer { get; set; }
    [Column("mobilnummer")] public string? Mobilnummer { get; set; }
    [Column("epost")] public string? Epost { get; set; }
    [Column("adresse")] public string? Adresse { get; set; }
    [Column("postnummer")] public string? Postnummer { get; set; }
    [Column("poststed")] public string? Poststed { get; set; }

    // B. Medsøker
    [Column("har_medsoker")] public bool HarMedsoker { get; set; }
    [Column("medsoker_navn")] public string? MedsokerNavn { get; set; }
    [Column("medsoker_foedselsnummer")] public string? MedsokerFoedselsnummer { get; set; }
    [Column("medsoker_mobil")] public string? MedsokerMobil { get; set; }
    [Column("medsoker_epost")] public string? MedsokerEpost { get; set; }
    [Column("medsoker_adresse")] public string? MedsokerAdresse { get; set; }
    [Column("medsoker_postnummer")] public string? MedsokerPostnummer { get; set; }
    [Column("medsoker_poststed")] public string? MedsokerPoststed { get; set; }
    [Column("medsoker_inntekt")] public decimal? MedsokerInntekt { get; set; }
    [Column("medsoker_arbeidsforhold")] public string? MedsokerArbeidsforhold { get; set; }

    // C. Husholdning
    [Column("statsborgerskap")] public string? Statsborgerskap { get; set; }
    [Column("opprinnelsesland")] public string? Opprinnelsesland { get; set; }
    [Column("aar_bodd_i_norge")] public int? AarBoddINorge { get; set; }
    [Column("sivilstatus")] public string? Sivilstatus { get; set; }
    [Column("antall_barn_under_18")] public int? AntallBarnUnder18 { get; set; }
    [Column("boforhold")] public string? Boforhold { get; set; }
    [Column("botid_mnd")] public int? BotidMnd { get; set; }
    [Column("antall_biler")] public int? AntallBiler { get; set; }

    // D. Arbeid og inntekt
    [Column("arbeidssituasjon")] public string? Arbeidssituasjon { get; set; }
    [Column("arbeidsgiver")] public string? Arbeidsgiver { get; set; }
    [Column("ansiennitet_mnd")] public int? AnsiennitetMnd { get; set; }
    [Column("utdanning")] public string? Utdanning { get; set; }
    [Column("aarsinntekt_brutto")] public decimal? AarsinntektBrutto { get; set; }
    [Column("andre_inntekter")] public decimal? AndreInntekter { get; set; }
    [Column("ektefelle_inntekt")] public decimal? EktefelleInntekt { get; set; }

    // E. Utgifter og forpliktelser
    [Column("boligkostnad_mnd")] public decimal? BoligkostnadMnd { get; set; }
    [Column("barnebidrag_betalt_mnd")] public decimal? BarnebidragBetaltMnd { get; set; }

    // F. Gjeld
    [Column("boliggjeld")] public decimal? Boliggjeld { get; set; }
    [Column("studielaan")] public decimal? Studielaan { get; set; }
    [Column("billaan")] public decimal? Billaan { get; set; }
    [Column("forbruksgjeld")] public decimal? Forbruksgjeld { get; set; }
    [Column("refinansieres_belop")] public decimal? RefinansieresBelop { get; set; }
    [Column("aktiv_inkasso")] public bool AktivInkasso { get; set; }

    // G. Lånedetaljer
    [Column("onsket_laanebelop")] public decimal? OnsketLaanebelop { get; set; }
    [Column("onsket_lopetid_mnd")] public int? OnsketLopetidMnd { get; set; }
    [Column("laanetype")] public string? Laanetype { get; set; }
    [Column("laaneformal")] public string? Laaneformal { get; set; }
    [Column("laaneformal_kode")] public string? LaaneformalKode { get; set; }
    [Column("naavaerende_rente")] public decimal? NaavaerendeRente { get; set; }

    // Skjema / tjeneste / samtykke (rentekutt-payload)
    [Column("tjeneste")] public string? Tjeneste { get; set; }
    [Column("tjeneste_kode")] public string? TjenesteKode { get; set; }
    [Column("skjema_versjon")] public int? SkjemaVersjon { get; set; }
    [Column("samtykke_gjeldsregister_kredittsjekk")] public bool SamtykkeGjeldsregisterKredittsjekk { get; set; }
    [Column("samlet_gjeld")] public decimal? SamletGjeld { get; set; }

    // Kode-varianter (maskinlesbare koder ved siden av de tekstlige verdiene)
    [Column("statsborgerskap_kode")] public string? StatsborgerskapKode { get; set; }
    [Column("sivilstatus_kode")] public string? SivilstatusKode { get; set; }
    [Column("boforhold_kode")] public string? BoforholdKode { get; set; }
    [Column("arbeidssituasjon_kode")] public string? ArbeidssituasjonKode { get; set; }
    [Column("utdanning_kode")] public string? UtdanningKode { get; set; }
    [Column("medsoker_arbeidssituasjon_kode")] public string? MedsokerArbeidssituasjonKode { get; set; }

    // Ja/nei-flagg fra payloaden
    [Column("har_andre_inntekter")] public bool HarAndreInntekter { get; set; }
    [Column("har_ektefelle_samboer_inntekt")] public bool HarEktefelleSamboerInntekt { get; set; }
    [Column("betaler_barnebidrag")] public bool BetalerBarnebidrag { get; set; }

    // H. Utbetaling
    [Column("kontonummer")] public string? Kontonummer { get; set; }

    // Bank-refinansiering (fra leads)
    [Column("navarende_bank")] public string? NavarendeBank { get; set; }
    [Column("kommune")] public string? Kommune { get; set; }
    [Column("fylke")] public string? Fylke { get; set; }
    [Column("boligverdi")] public decimal? Boligverdi { get; set; }

    [Column("notater")] public string? Notater { get; set; }
    [Column("kilde")] public string? Kilde { get; set; }
    [Column("delegert_bank")] public string? DelegertBank { get; set; }

    [Column("status")] public string Status { get; set; } = "Åpen";

    // Eierskap til saken
    [Column("eier")] public string? Eier { get; set; }
    [Column("eier_navn")] public string? EierNavn { get; set; }
    [Column("eier_tatt_at")] public DateTime? EierTattAt { get; set; }

    // Oppfølging (egne saker)
    [Column("siste_kontakt")] public DateTime? SisteKontakt { get; set; }
    [Column("neste_oppfolging")] public DateTime? NesteOppfolging { get; set; }

    // Tidsstempler — leses for sortering, men skrives ikke (DB styrer dem).
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime UpdatedAt { get; set; }
}
