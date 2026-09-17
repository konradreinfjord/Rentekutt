namespace RentkuttCRM.Services;

/// <summary>
/// Produktkategoriene brukt i Markedsinnsikt (og på kundekortet). Kundekortets
/// <see cref="Kundekort.Produktkategori"/> holder en av disse verdiene når den er satt manuelt;
/// er den tom, utledes kategorien automatisk fra lånetype/alder/kundetype via <see cref="Foreslaa"/>.
/// </summary>
public static class Produktkategori
{
    public const string Boliglaan = "Boliglån";
    public const string BoliglaanUng = "Boliglån ung (under 34)";
    public const string Forstehjem = "Førstehjemslån";
    public const string Forbrukslaan = "Forbrukslån";
    public const string Spesialbank = "Spesialbank";
    public const string Rammelaan = "Rammelån";
    public const string Bedriftslaan = "Bedriftslån";
    public const string Kassakreditt = "Kassakreditt";

    /// <summary>Alle kategorier i visningsrekkefølge — Boliglån først (default valgt i Markedsinnsikt).</summary>
    public static readonly string[] Alle =
    {
        Boliglaan, BoliglaanUng, Forstehjem, Forbrukslaan, Spesialbank, Rammelaan, Bedriftslaan, Kassakreditt,
    };

    /// <summary>Auto-forslag ut fra tilgjengelige signaler. Brukes som default når feltet ikke er satt.
    /// Dekker Boliglån/Boliglån ung/Forbrukslån/Bedriftslån; de fire øvrige settes manuelt på kundekortet.</summary>
    public static string Foreslaa(Kundekort k)
    {
        var t = (k.Laanetype ?? "").ToLowerInvariant();

        // Bedrift: kassakreditt hvis lånetypen sier det, ellers bedriftslån.
        if (k.KundeType == "B2B")
            return t.Contains("kassa") || t.Contains("kasse") ? Kassakreditt : Bedriftslaan;

        // Privat: styr etter LÅNETYPE. Eksplisitte typer først.
        if (t.Contains("førstehjem") || t.Contains("forstehjem")) return Forstehjem;
        if (t.Contains("rammel")) return Rammelaan;
        if (t.Contains("kassa") || t.Contains("kasse")) return Kassakreditt;
        if (t.Contains("bolig")) return ErUng(k) ? BoliglaanUng : Boliglaan;
        // «Kredittkort»/eksplisitt forbruk = usikret → forbrukslån.
        if (t.Contains("forbruk") || t.Contains("kredittkort")) return Forbrukslaan;

        // «Refinansiering» og ukjent lånetype er tvetydige: refinansiering av boliglån (sikret,
        // store beløp) hører til Boliglån, mens refinansiering av smågjeld er forbrukslån. Skill
        // på boligindikatorer — boliggjeld/boligverdi registrert, eller lånebeløp over
        // forbrukslånstaket (~1 MNOK). Datagrunnlaget viser at ~alle «Refinansiering» er boliglån.
        if (ErBoligindikert(k)) return ErUng(k) ? BoliglaanUng : Boliglaan;
        return Forbrukslaan;
    }

    // Boligindikator: registrert boliggjeld/boligverdi, eller lånebeløp over forbrukslånstaket.
    // Forbrukslån i Norge ligger typisk under ~600k; et lån på ≥ 1 MNOK er reelt et boliglån.
    private static bool ErBoligindikert(Kundekort k) =>
        (k.Boliggjeld ?? 0) > 0 || (k.Boligverdi ?? 0) > 0 || (k.OnsketLaanebelop ?? 0) >= 1_000_000m;

    // «Ung» = født 1993 eller senere (samme definisjon som «Boliglån ung»-merknaden).
    private static bool ErUng(Kundekort k) => BeregningService.FnrFodselsaar(k.Foedselsnummer) is int a && a >= 1993;

    /// <summary>Effektiv kategori for et kort: manuelt satt verdi hvis den finnes, ellers auto-forslag.</summary>
    public static string Effektiv(Kundekort k)
        => string.IsNullOrWhiteSpace(k.Produktkategori) ? Foreslaa(k) : k.Produktkategori!.Trim();
}
