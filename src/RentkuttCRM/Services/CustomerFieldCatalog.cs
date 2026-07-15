namespace RentkuttCRM.Services;

public enum FieldType { Text, Number, Enum }

/// <summary>
/// Definisjon av ett kundedatafelt som kan brukes i logikk-matrisen (universalfilter).
/// </summary>
public record FieldDef(
    string Key,
    string Label,
    string Category,
    FieldType Type,
    string[]? Options = null,
    string? Unit = null,
    string? Placeholder = null);

/// <summary>
/// Sentralt katalog over alle kundedatafelt vi kan filtrere/segmentere på.
///
/// Dette er ÉN kilde til sannhet for universalfilteret. Når ekte datafelt
/// opprettes, oppdateres listen her (eller byttes til API-henting i
/// <see cref="Fields"/>) — UI-en i logikk-matrisen oppdateres da automatisk.
///
/// TODO (når API er klart): erstatt den hardkodede listen med et kall mot
/// data-API-et (PSD2/Kreditz + scoring) som returnerer feltskjemaet dynamisk.
/// </summary>
public class CustomerFieldCatalog
{
    public IReadOnlyList<FieldDef> Fields { get; } = new List<FieldDef>
    {
        // ---- Geografi / område ----
        new("postnummer", "Postnummer", "Geografi", FieldType.Text, Placeholder: "f.eks. 0150 eller 0150–0299"),
        new("poststed",   "Poststed",   "Geografi", FieldType.Text, Placeholder: "f.eks. Oslo"),
        new("kommune",    "Kommune",    "Geografi", FieldType.Text, Placeholder: "f.eks. Bergen"),
        new("fylke",      "Fylke",      "Geografi", FieldType.Enum,
            Options: new[] { "Oslo", "Akershus", "Vestland", "Trøndelag", "Rogaland", "Innlandet", "Agder", "Nordland", "Annet" }),
        new("landsdel",   "Landsdel",   "Geografi", FieldType.Enum,
            Options: new[] { "Østlandet", "Vestlandet", "Sørlandet", "Midt-Norge", "Nord-Norge" }),

        // ---- Lån ----
        new("laanetype",  "Lånetype",   "Lån", FieldType.Enum,
            Options: new[] { "Forbrukslån", "Boliglån", "Refinansiering", "Billån", "Mellomfinansiering" }),
        new("laanebelop", "Lånebeløp",  "Lån", FieldType.Number, Unit: "kr", Placeholder: "f.eks. 3 000 000"),
        new("lopetid",    "Løpetid",    "Lån", FieldType.Number, Unit: "mnd", Placeholder: "f.eks. 240"),
        new("formaal",    "Formål",     "Lån", FieldType.Enum,
            Options: new[] { "Kjøp bolig", "Refinansiere gjeld", "Oppussing", "Kjøp bil", "Annet" }),
        new("naavaerende_bank", "Nåværende bank", "Lån", FieldType.Text, Placeholder: "f.eks. Santander"),

        // ---- Økonomi ----
        new("gjeldsgrad",   "Gjeldsgrad",          "Økonomi", FieldType.Number, Unit: "%", Placeholder: "f.eks. 400"),
        new("belaaningsgrad","Belåningsgrad",      "Økonomi", FieldType.Number, Unit: "%", Placeholder: "f.eks. 85"),
        new("kredittscore", "Kredittscore",        "Økonomi", FieldType.Number, Placeholder: "0–1000"),
        new("aarsinntekt",  "Årsinntekt",          "Økonomi", FieldType.Number, Unit: "kr", Placeholder: "f.eks. 650 000"),
        new("anmerkninger", "Betalingsanmerkninger","Økonomi", FieldType.Enum, Options: new[] { "Ja", "Nei" }),

        // ---- Personalia ----
        new("alder",        "Alder",         "Personalia", FieldType.Number, Unit: "år", Placeholder: "f.eks. 35"),
        new("sivilstatus",  "Sivilstatus",   "Personalia", FieldType.Enum,
            Options: new[] { "Enslig", "Samboer", "Gift", "Skilt" }),
        new("ansettelse",   "Ansettelsesform","Personalia", FieldType.Enum,
            Options: new[] { "Fast", "Midlertidig", "Selvstendig", "Pensjonist", "Student" }),
        new("boligstatus",  "Boligstatus",   "Personalia", FieldType.Enum, Options: new[] { "Eier", "Leier" }),

        // ---- PSD2 / Kreditz ----
        new("saldo_brukskonto", "Saldo brukskonto", "Kontodata (PSD2)", FieldType.Number, Unit: "kr"),
        new("manedlig_forbruk", "Månedlig forbruk", "Kontodata (PSD2)", FieldType.Number, Unit: "kr"),
        new("eksisterende_laan","Eksisterende lån", "Kontodata (PSD2)", FieldType.Number, Unit: "kr"),
    };

    public FieldDef? Get(string key) => Fields.FirstOrDefault(f => f.Key == key);

    public IEnumerable<string> Categories => Fields.Select(f => f.Category).Distinct();

    public IEnumerable<FieldDef> InCategory(string category) => Fields.Where(f => f.Category == category);

    public static string[] OperatorsFor(FieldType type) => type switch
    {
        FieldType.Number => new[] { "=", "≠", ">", "<", "mellom" },
        FieldType.Enum   => new[] { "=", "≠", "er en av" },
        _                => new[] { "=", "≠", "inneholder", "starter med", "mellom" },
    };
}
