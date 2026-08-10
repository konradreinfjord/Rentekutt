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
        if (k.KundeType == "B2B") return Bedriftslaan;

        var erBolig = (k.Laanetype ?? "").Contains("bolig", StringComparison.OrdinalIgnoreCase)
                      || (k.Boligverdi ?? 0) > 0;
        if (erBolig)
        {
            var (alder, _) = BeregningService.FnrInfo(k.Foedselsnummer);
            return alder is int a && a < 34 ? BoliglaanUng : Boliglaan;
        }
        return Forbrukslaan;
    }

    /// <summary>Effektiv kategori for et kort: manuelt satt verdi hvis den finnes, ellers auto-forslag.</summary>
    public static string Effektiv(Kundekort k)
        => string.IsNullOrWhiteSpace(k.Produktkategori) ? Foreslaa(k) : k.Produktkategori!.Trim();
}
