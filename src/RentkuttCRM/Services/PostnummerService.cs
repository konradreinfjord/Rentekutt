using System.Globalization;
using System.Reflection;

namespace RentkuttCRM.Services;

/// <summary>
/// Slår opp kommune, poststed og fylke fra postnummer basert på Postens/Brings
/// postnummerregister (embedded ressurs Data/postnummer.tsv: postnr, poststed, kommune).
/// Brukes til å berike innkommende søknader når kommune/poststed mangler.
/// </summary>
public class PostnummerService
{
    private readonly ILogger<PostnummerService> _log;
    // postnr (4 siffer) → (Kommune, Poststed) med pen kasusstilling.
    private readonly Dictionary<string, (string Kommune, string Poststed)> _oppslag = new();

    public int Antall => _oppslag.Count;

    public PostnummerService(ILogger<PostnummerService> log)
    {
        _log = log;
        Last();
    }

    private void Last()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var navn = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("postnummer.tsv", StringComparison.OrdinalIgnoreCase));
            if (navn is null) { _log.LogWarning("Fant ikke postnummer.tsv-ressursen"); return; }
            using var stream = asm.GetManifestResourceStream(navn)!;
            using var sr = new StreamReader(stream);
            string? linje;
            while ((linje = sr.ReadLine()) is not null)
            {
                var p = linje.Split('\t');
                if (p.Length < 3) continue;
                var postnr = p[0].Trim();
                if (postnr.Length == 0) continue;
                _oppslag[postnr] = (PenKasus(p[2]), PenKasus(p[1]));
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Lasting av postnummerregister feilet"); }
    }

    /// <summary>Kommune for et postnummer, eller null hvis ukjent.</summary>
    public string? Kommune(string? postnummer) => Slaa(postnummer)?.Kommune;

    /// <summary>Poststed for et postnummer, eller null hvis ukjent.</summary>
    public string? Poststed(string? postnummer) => Slaa(postnummer)?.Poststed;

    /// <summary>Fylke utledet av kommunen for postnummeret, eller null.</summary>
    public string? Fylke(string? postnummer)
    {
        var kommune = Kommune(postnummer);
        return string.IsNullOrEmpty(kommune) ? null : NorskeKommuner.Fylke(kommune);
    }

    private (string Kommune, string Poststed)? Slaa(string? postnummer)
    {
        var pn = new string((postnummer ?? "").Where(char.IsDigit).ToArray());
        if (pn.Length != 4) return null;
        return _oppslag.TryGetValue(pn, out var v) ? v : null;
    }

    // «OSLO» → «Oslo», «NORD-AURDAL» → «Nord-Aurdal», «NES I ÅDAL» → «Nes i Ådal».
    private static readonly HashSet<string> SmåOrd = new(StringComparer.OrdinalIgnoreCase) { "i", "og", "på", "ved", "for" };
    private static string PenKasus(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        var deler = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < deler.Length; i++)
        {
            if (i > 0 && SmåOrd.Contains(deler[i])) { deler[i] = deler[i].ToLower(CultureInfo.CurrentCulture); continue; }
            // Håndter bindestrek: NORD-AURDAL → Nord-Aurdal
            deler[i] = string.Join("-", deler[i].Split('-').Select(OrdKasus));
        }
        return string.Join(" ", deler);
    }

    private static string OrdKasus(string ord) =>
        ord.Length == 0 ? ord : char.ToUpper(ord[0], CultureInfo.CurrentCulture) + ord[1..].ToLower(CultureInfo.CurrentCulture);
}
