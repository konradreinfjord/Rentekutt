using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>Én rad i refinansiering-mappingen: hvilken lånetype + Instabank-produkt en
/// gitt «refinansieres_laanetype» (Begge/Forbrukslån/Kredittkort) skal gi.</summary>
public class RefiMapRad
{
    public string Verdi { get; set; } = "";       // normalisert nøkkel: "begge" | "forbrukslaan" | "kredittkort"
    public string Etikett { get; set; } = "";     // visningsnavn
    public string Laanetype { get; set; } = "";   // lånetype som settes på leadet
    public int ProduktKode { get; set; }          // Instabank-produktkode saken skal sendes på
}

/// <summary>
/// Konfigurerbar mapping for refinansiering av gjeld: «refinansieres_laanetype» → lånetype + Instabank-produkt.
/// Erstatter hardkodet logikk, og styres fra Logikk-matrise i Admin. Lagres som JSON i innstillinger.
/// </summary>
public class RefinansieringMappingService
{
    private readonly SettingsService _settings;
    private const string Key = "refinansiering_mapping";

    public RefinansieringMappingService(SettingsService settings) => _settings = settings;

    public static List<RefiMapRad> Standard() => new()
    {
        new() { Verdi = "begge",        Etikett = "Begge (forbrukslån + kredittkort)", Laanetype = "Forbrukslån", ProduktKode = InstabankService.ProduktForbrukslaan },
        new() { Verdi = "forbrukslaan", Etikett = "Forbrukslån",                        Laanetype = "Forbrukslån", ProduktKode = InstabankService.ProduktForbrukslaan },
        new() { Verdi = "kredittkort",  Etikett = "Kredittkort",                        Laanetype = "Kredittkort", ProduktKode = InstabankService.ProduktKredittkort },
    };

    public async Task<List<RefiMapRad>> HentAsync()
    {
        var json = await _settings.GetAsync(Key);
        if (string.IsNullOrWhiteSpace(json)) return Standard();
        try
        {
            var lagret = JsonSerializer.Deserialize<List<RefiMapRad>>(json);
            return lagret is { Count: > 0 } ? lagret : Standard();
        }
        catch { return Standard(); }
    }

    public Task LagreAsync(List<RefiMapRad> rader) => _settings.SetAsync(Key, JsonSerializer.Serialize(rader));

    /// <summary>Normaliser en innkommende «refinansieres_laanetype(_kode)» til config-nøkkel.</summary>
    public static string NormVerdi(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        if (v.Contains("kredittkort") || v.Contains("kreditt")) return "kredittkort";
        if (v.Contains("begge") || v.Contains("both")) return "begge";
        return "forbrukslaan"; // forbrukslån/forbrukslaan/consumer m.m.
    }

    /// <summary>Lånetypen en innkommende refinansiering-verdi skal gi (null = ingen match).</summary>
    public static string? LaanetypeFor(IEnumerable<RefiMapRad> cfg, string? refiRaw)
    {
        if (string.IsNullOrWhiteSpace(refiRaw)) return null;
        var key = NormVerdi(refiRaw);
        return cfg.FirstOrDefault(r => r.Verdi == key)?.Laanetype;
    }

    /// <summary>Instabank-produktkoden en lånetype er mappet til (null = ingen match).</summary>
    public static int? ProduktForLaanetype(IEnumerable<RefiMapRad> cfg, string? laanetype)
    {
        if (string.IsNullOrWhiteSpace(laanetype)) return null;
        var treff = cfg.FirstOrDefault(r => string.Equals(r.Laanetype, laanetype, StringComparison.OrdinalIgnoreCase));
        return treff is { ProduktKode: > 0 } ? treff.ProduktKode : null;
    }
}
