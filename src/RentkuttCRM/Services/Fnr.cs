namespace RentkuttCRM.Services;

/// <summary>Validering av norske fødselsnummer/D-nummer: 11 sifre, gyldig datoprefiks
/// (inkl. D-nummer der dag er +40) og korrekte kontrollsiffer (modulus-11).</summary>
public static class Fnr
{
    public static bool ErGyldig(string? fnr)
    {
        var d = new string((fnr ?? "").Where(char.IsDigit).ToArray());
        if (d.Length != 11) return false;
        var dag = (d[0] - '0') * 10 + (d[1] - '0');
        var mnd = (d[2] - '0') * 10 + (d[3] - '0');
        if (dag > 40) dag -= 40;                 // D-nummer: dag er +40
        if (dag is < 1 or > 31 || mnd is < 1 or > 12) return false;
        return Mod11(d);
    }

    private static bool Mod11(string f)
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
