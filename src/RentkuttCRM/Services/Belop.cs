using System.Globalization;

namespace RentkuttCRM.Services;

/// <summary>Formatering/parsing av kronebeløp med tusenskille (mellomrom), f.eks. 4 000 000.
/// Brukes av KroneInput slik at beløp er lette å lese og vanskelige å skrive feil.</summary>
public static class Belop
{
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    /// <summary>Hele kroner med mellomrom som tusenskille: 100, 10 000, 5 000 000.</summary>
    public static string Format(decimal v)
        => v.ToString("#,##0", Nb).Replace(' ', ' ').Replace(' ', ' ');

    /// <summary>Tolker tekst (med mellomrom/punktum som tusenskille, komma som desimal) til beløp.</summary>
    public static decimal? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => char.IsDigit(c) || c is ',' or '.').ToArray());
        // Komma = desimalskille (nb). Punktum behandles som tusenskille og fjernes.
        clean = clean.Contains(',') ? clean.Replace(".", "").Replace(',', '.') : clean.Replace(".", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
