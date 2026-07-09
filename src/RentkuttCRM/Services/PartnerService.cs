using Newtonsoft.Json;
using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("partnere")]
public class Partner : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("navn")] public string Navn { get; set; } = "";
    [Column("provisjon")] public string? Provisjon { get; set; }
    [Column("engangssum")] public string? Engangssum { get; set; }

    // Postnummer-dekning som fritekst: kommaseparerte postnummer og/eller intervaller,
    // f.eks. "0001-1299, 5000, 7000-7099". Brukes til å foreslå bank ut fra kundens postnummer.
    [Column("postnummer_dekning")] public string? PostnummerDekning { get; set; }

    // Automatisk sending: true = matchende søknader rutes automatisk til banken.
    // false = banken vises som «foreslått bank» i markedet (menneske bestemmer).
    [Column("auto_send")] public bool AutoSend { get; set; }

    // Transient (ikke persistert) — metode-valg i API-fanen. JsonIgnore så den
    // ikke sendes til databasen (ingen Method-kolonne der).
    [JsonIgnore] public string Method { get; set; } = "Webhook";
}

/// <summary>Bankpartnere lagret i databasen (med staging-fallback).</summary>
public class PartnerService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<PartnerService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Partner> _staging = new();
    private bool _initialized;

    public PartnerService(Supabase.Client client, IConfiguration cfg, ILogger<PartnerService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<Partner>> ListAsync()
    {
        if (!IsConfigured) return _staging.ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Partner>().Order(x => x.Navn, Constants.Ordering.Ascending, Constants.NullPosition.Last).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av partnere feilet"); return new(); }
    }

    public async Task<(bool ok, string? error)> AddAsync(string navn, string? provisjon, string? engangssum, string? postnummerDekning = null)
    {
        navn = (navn ?? "").Trim();
        if (string.IsNullOrWhiteSpace(navn)) return (false, "Banknavn er påkrevd.");
        var p = new Partner
        {
            Navn = navn,
            Provisjon = provisjon,
            Engangssum = engangssum,
            PostnummerDekning = string.IsNullOrWhiteSpace(postnummerDekning) ? null : postnummerDekning.Trim(),
        };
        if (!IsConfigured)
        {
            if (_staging.Any(x => x.Navn.Equals(navn, StringComparison.OrdinalIgnoreCase))) return (false, "Partneren finnes allerede.");
            p.Id = Guid.NewGuid();
            _staging.Add(p);
            return (true, null);
        }
        try
        {
            await EnsureInitAsync();
            var finnes = await _client.From<Partner>().Where(x => x.Navn == navn).Get();
            if (finnes.Models.Count > 0) return (false, "Partneren finnes allerede.");
            await _client.From<Partner>().Insert(p);
            return (true, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av partner feilet"); return (false, "Teknisk feil ved lagring."); }
    }

    public async Task UpdateCompAsync(Guid id, string? provisjon, string? engangssum)
    {
        if (!IsConfigured)
        {
            var p = _staging.FirstOrDefault(x => x.Id == id);
            if (p is not null) { p.Provisjon = provisjon; p.Engangssum = engangssum; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Partner>().Where(x => x.Id == id)
                .Set(x => x.Provisjon!, provisjon ?? "")
                .Set(x => x.Engangssum!, engangssum ?? "")
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av kompensasjon feilet"); }
    }

    public async Task UpdatePostnummerDekningAsync(Guid id, string? dekning)
    {
        dekning = string.IsNullOrWhiteSpace(dekning) ? null : dekning.Trim();
        if (!IsConfigured)
        {
            var p = _staging.FirstOrDefault(x => x.Id == id);
            if (p is not null) p.PostnummerDekning = dekning;
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Partner>().Where(x => x.Id == id)
                .Set(x => x.PostnummerDekning!, dekning ?? "")
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av postnummer-dekning feilet"); }
    }

    public async Task UpdateAutoSendAsync(Guid id, bool autoSend)
    {
        if (!IsConfigured)
        {
            var p = _staging.FirstOrDefault(x => x.Id == id);
            if (p is not null) p.AutoSend = autoSend;
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Partner>().Where(x => x.Id == id)
                .Set(x => x.AutoSend, autoSend)
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av auto-send feilet"); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}

/// <summary>
/// Matcher kundens postnummer mot bankenes postnummer-dekning.
/// Dekning skrives som fritekst med kommaseparerte postnummer og/eller intervaller,
/// f.eks. "0001-1299, 5000, 7000-7099". Whitespace, semikolon og linjeskift går også som skilletegn.
/// </summary>
public static class BankDekning
{
    // Én token er enten et enkelt postnummer eller et intervall Fra–Til (inklusivt).
    private static IEnumerable<(int Fra, int Til)> ParseIntervaller(string? dekning)
    {
        if (string.IsNullOrWhiteSpace(dekning)) yield break;
        foreach (var token in dekning.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Godta både bindestrek og tankestrek som intervall-skille.
            var deler = token.Split(new[] { '-', '–', '—' }, StringSplitOptions.RemoveEmptyEntries);
            if (deler.Length == 1 && TryPostnr(deler[0], out var n))
                yield return (n, n);
            else if (deler.Length == 2 && TryPostnr(deler[0], out var fra) && TryPostnr(deler[1], out var til))
                yield return fra <= til ? (fra, til) : (til, fra);
        }
    }

    private static bool TryPostnr(string s, out int n)
    {
        // Behold kun sifre — tåler "0150", "0150 Oslo", osv.
        var rene = new string(s.Where(char.IsDigit).ToArray());
        if (rene.Length is >= 1 and <= 4 && int.TryParse(rene, out n)) return true;
        n = 0;
        return false;
    }

    public static bool Dekker(string? dekning, string? postnummer)
    {
        if (!TryPostnr(postnummer ?? "", out var p)) return false;
        foreach (var (fra, til) in ParseIntervaller(dekning))
            if (p >= fra && p <= til) return true;
        return false;
    }

    /// <summary>Navnene på bankene som dekker det gitte postnummeret (alfabetisk).</summary>
    public static List<string> ForeslaBanker(string? postnummer, IEnumerable<Partner> banker)
    {
        if (!TryPostnr(postnummer ?? "", out _)) return new();
        return banker
            .Where(b => Dekker(b.PostnummerDekning, postnummer))
            .Select(b => b.Navn)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
