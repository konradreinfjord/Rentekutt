using System.Text.RegularExpressions;

namespace RentkuttCRM.Services;

/// <summary>
/// Sikkerhetsnett mot fødselsnummer i logg. Maskerer 11-sifrede sekvenser KUN når de
/// faktisk ser ut som et norsk fødselsnummer: gyldig datoprefiks (dag 01–31, måned 01–12,
/// inkludert D-nummer der dag er +40) OG bestått MOD11-kontroll. Da rammes fnr og nesten
/// ingenting annet — spesifikt IKKE kontonummer (også 11 sifre, men består sjelden begge krav).
/// </summary>
public static partial class FnrRedactor
{
    public const string Maske = "[fnr-redigert]";

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")]
    private static partial Regex ElleveSifre();

    public static string Redact(string? tekst)
    {
        if (string.IsNullOrEmpty(tekst)) return tekst ?? "";
        return ElleveSifre().Replace(tekst, m => ErFodselsnummer(m.Value) ? Maske : m.Value);
    }

    private static bool ErFodselsnummer(string d)
    {
        var dag = (d[0] - '0') * 10 + (d[1] - '0');
        var mnd = (d[2] - '0') * 10 + (d[3] - '0');
        if (dag > 40) dag -= 40;               // D-nummer: dag er +40
        if (dag is < 1 or > 31 || mnd is < 1 or > 12) return false;
        return Mod11Ok(d);
    }

    private static bool Mod11Ok(string f)
    {
        ReadOnlySpan<int> v1 = [3, 7, 6, 1, 8, 9, 4, 5, 2];
        ReadOnlySpan<int> v2 = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var s1 = 0;
        for (var i = 0; i < 9; i++) s1 += (f[i] - '0') * v1[i];
        var k1 = 11 - (s1 % 11);
        if (k1 == 11) k1 = 0;
        if (k1 == 10 || k1 != f[9] - '0') return false;
        var s2 = 0;
        for (var i = 0; i < 10; i++) s2 += (f[i] - '0') * v2[i];
        var k2 = 11 - (s2 % 11);
        if (k2 == 11) k2 = 0;
        return k2 != 10 && k2 == f[10] - '0';
    }
}
