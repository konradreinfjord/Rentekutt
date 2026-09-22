using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>
/// Merknadsregel: gir kundekort en fargebadge (f.eks. grønn «Boliglån UNG» / «Grønt boliglån»)
/// når betingelsen (felt/operator/verdi) matcher. Samme betingelsesmodell som <see cref="Rutingsregel"/>;
/// evalueres med <see cref="RutingEval.Matcher(string?, string?, string?, Kundekort)"/>.
/// </summary>
[Table("merknadsregel")]
public class Merknadsregel : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("prioritet")] public int Prioritet { get; set; } = 1;
    [Column("felt_nokkel")] public string FeltNokkel { get; set; } = "";
    [Column("operator")] public string Operator { get; set; } = "=";
    [Column("verdi")] public string Verdi { get; set; } = "";
    [Column("badge_tekst")] public string BadgeTekst { get; set; } = "";
    [Column("badge_farge")] public string BadgeFarge { get; set; } = "gronn";
    [Column("aktiv")] public bool Aktiv { get; set; } = true;
    // Ekstra OG-premisser (JSON-array). Hovedbetingelsen over må matche + ALLE disse.
    [Column("tilleggspremisser")] public string? Tilleggspremisser { get; set; }
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; }

    // Parset liste av tilleggspremisser (ikke persistert direkte — utledet fra JSON-kolonnen).
    [Newtonsoft.Json.JsonIgnore]
    public List<Mpremiss> TilleggsListe => MerknadsregelService.ParsePremisser(Tilleggspremisser);
}

/// <summary>Én ekstra OG-betingelse på en merknadsregel (felt/operator/verdi).</summary>
public record Mpremiss(string FeltNokkel, string Operator, string Verdi);

public class MerknadsregelService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<MerknadsregelService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Merknadsregel> _staging = new();
    private bool _initialized;

    public MerknadsregelService(Supabase.Client client, IConfiguration cfg, ILogger<MerknadsregelService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<Merknadsregel>> ListAsync()
    {
        if (!IsConfigured) return _staging.OrderBy(r => r.Prioritet).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Merknadsregel>()
                .Order(x => x.Prioritet, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av merknadsregler feilet"); return new(); }
    }

    public async Task<(Merknadsregel? Regel, string? Feil)> AddAsync(int prioritet, string feltNokkel, string @operator, string verdi, string badgeTekst, string badgeFarge, IEnumerable<Mpremiss>? tilleggspremisser = null)
    {
        var regel = new Merknadsregel
        {
            Prioritet = prioritet,
            FeltNokkel = feltNokkel,
            Operator = @operator,
            Verdi = verdi,
            BadgeTekst = (badgeTekst ?? "").Trim(),
            BadgeFarge = string.IsNullOrWhiteSpace(badgeFarge) ? "gronn" : badgeFarge,
            Tilleggspremisser = SerialiserPremisser(tilleggspremisser),
            Aktiv = true,
        };
        if (string.IsNullOrWhiteSpace(regel.BadgeTekst)) return (null, "Badge-tekst er påkrevd.");
        if (!IsConfigured) { regel.Id = Guid.NewGuid(); _staging.Add(regel); return (regel, null); }
        try
        {
            await EnsureInitAsync();
            var lagret = (await _client.From<Merknadsregel>().Insert(regel)).Models.FirstOrDefault();
            return (lagret, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Lagring av merknadsregel feilet"); return (null, ex.Message); }
    }

    public async Task SetAktivAsync(Guid id, bool aktiv)
    {
        if (!IsConfigured) { var r = _staging.FirstOrDefault(x => x.Id == id); if (r is not null) r.Aktiv = aktiv; return; }
        try { await EnsureInitAsync(); await _client.From<Merknadsregel>().Where(x => x.Id == id).Set(x => x.Aktiv, aktiv).Update(); }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av merknadsregel feilet"); }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return; }
        try { await EnsureInitAsync(); await _client.From<Merknadsregel>().Where(x => x.Id == id).Delete(); }
        catch (Exception ex) { _log.LogError(ex, "Sletting av merknadsregel feilet"); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }

    // Tilleggspremisser lagres kompakt som JSON: [{"f":"laanetype","o":"inneholder","v":"bolig"}].
    public static string? SerialiserPremisser(IEnumerable<Mpremiss>? premisser)
    {
        var liste = (premisser ?? Enumerable.Empty<Mpremiss>())
            .Where(p => !string.IsNullOrWhiteSpace(p.FeltNokkel))
            .Select(p => new Dictionary<string, string> { ["f"] = p.FeltNokkel, ["o"] = p.Operator, ["v"] = p.Verdi })
            .ToList();
        return liste.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(liste);
    }

    public static List<Mpremiss> ParsePremisser(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var liste = new List<Mpremiss>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string Les(string navn) => el.TryGetProperty(navn, out var v) ? v.GetString() ?? "" : "";
                var felt = Les("f");
                if (string.IsNullOrWhiteSpace(felt)) continue;
                var op = Les("o"); if (string.IsNullOrWhiteSpace(op)) op = "=";
                liste.Add(new Mpremiss(felt, op, Les("v")));
            }
            return liste;
        }
        catch { return new(); }
    }
}

/// <summary>Evaluerer merknadsregler mot et kundekort og returnerer badgene som skal vises.</summary>
public static class MerknadEval
{
    public record Merknad(string Tekst, string FargeKey, string Hex);

    /// <summary>Fargevalg for badges (nøkkel → visningsnavn + hex). Grønn er standard.</summary>
    public static readonly (string Key, string Navn, string Hex)[] Farger =
    {
        ("gronn", "Grønn", "#2f8a3e"),
        ("bla", "Blå", "#1f5fc4"),
        ("gul", "Gul/amber", "#c2740a"),
        ("rod", "Rød", "#b42318"),
        ("lilla", "Lilla", "#6b3fc4"),
        ("graa", "Grå", "#5b6b7f"),
    };

    public static string FargeHex(string? key) =>
        Farger.FirstOrDefault(f => f.Key == key).Hex is { Length: > 0 } h ? h : "#2f8a3e";

    /// <summary>Badgene som matcher kundekortet (aktive regler, prioritert rekkefølge, uten duplikater).</summary>
    public static List<Merknad> MerknaderFor(IEnumerable<Merknadsregel> regler, Kundekort k) =>
        regler.Where(r => r.Aktiv).OrderBy(r => r.Prioritet)
            // Hovedbetingelsen OG alle tilleggspremisser må matche (AND-logikk).
            .Where(r => RutingEval.Matcher(r.FeltNokkel, r.Operator, r.Verdi, k)
                        && r.TilleggsListe.All(p => RutingEval.Matcher(p.FeltNokkel, p.Operator, p.Verdi, k)))
            .Select(r => new Merknad(r.BadgeTekst.Trim(), r.BadgeFarge, FargeHex(r.BadgeFarge)))
            .Where(m => !string.IsNullOrWhiteSpace(m.Tekst))
            .GroupBy(m => m.Tekst, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
}
