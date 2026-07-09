using System.Globalization;
using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("rutingsregel")]
public class Rutingsregel : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("prioritet")] public int Prioritet { get; set; } = 1;
    [Column("felt_nokkel")] public string FeltNokkel { get; set; } = "";
    [Column("operator")] public string Operator { get; set; } = "=";
    [Column("verdi")] public string Verdi { get; set; } = "";
    [Column("banker")] public string Banker { get; set; } = "";
    [Column("aktiv")] public bool Aktiv { get; set; } = true;
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; }

    /// <summary>Banklista som liste (banker lagres komma-separert).</summary>
    public List<string> BankerListe =>
        (Banker ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public class RutingsregelService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<RutingsregelService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Rutingsregel> _staging = new();
    private bool _initialized;

    public RutingsregelService(Supabase.Client client, IConfiguration cfg, ILogger<RutingsregelService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<Rutingsregel>> ListAsync()
    {
        if (!IsConfigured) return _staging.OrderBy(r => r.Prioritet).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Rutingsregel>()
                .Order(x => x.Prioritet, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av rutingsregler feilet"); return new(); }
    }

    public async Task<Rutingsregel?> AddAsync(int prioritet, string feltNokkel, string @operator, string verdi, IEnumerable<string> banker)
    {
        var regel = new Rutingsregel
        {
            Prioritet = prioritet,
            FeltNokkel = feltNokkel,
            Operator = @operator,
            Verdi = verdi,
            Banker = string.Join(", ", banker),
            Aktiv = true,
        };
        if (!IsConfigured) { regel.Id = Guid.NewGuid(); _staging.Add(regel); return regel; }
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Rutingsregel>().Insert(regel)).Models.FirstOrDefault();
        }
        catch (Exception ex) { _log.LogError(ex, "Lagring av rutingsregel feilet"); return null; }
    }

    public async Task SetAktivAsync(Guid id, bool aktiv)
    {
        if (!IsConfigured) { var r = _staging.FirstOrDefault(x => x.Id == id); if (r is not null) r.Aktiv = aktiv; return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<Rutingsregel>().Where(x => x.Id == id).Set(x => x.Aktiv, aktiv).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av rutingsregel feilet"); }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<Rutingsregel>().Where(x => x.Id == id).Delete();
        }
        catch (Exception ex) { _log.LogError(ex, "Sletting av rutingsregel feilet"); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}

/// <summary>Evaluerer rutingsregler mot et kundekort for å foreslå bank(er).</summary>
public static class RutingEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Banker foreslått for kunden: fra matchende aktive regler, der banken IKKE auto-sendes.</summary>
    public static List<string> ForeslaBanker(IEnumerable<Rutingsregel> regler, Kundekort k, IEnumerable<Partner> partnere)
    {
        var autoSend = partnere.Where(p => p.AutoSend).Select(p => p.Navn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return regler.Where(r => r.Aktiv).OrderBy(r => r.Prioritet)
            .Where(r => Matcher(r, k))
            .SelectMany(r => r.BankerListe)
            .Where(b => !autoSend.Contains(b))         // auto-send på ⇒ auto-rutes, foreslås ikke
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool Matcher(Rutingsregel r, Kundekort k)
    {
        // Postnummer: intervall-/listeparsing («7000-8000», «5000, 7000-7099») uansett operator.
        if (r.FeltNokkel == "postnummer")
            return BankDekning.Dekker(r.Verdi, k.Postnummer);

        var verdi = KundeVerdi(k, r.FeltNokkel);
        if (string.IsNullOrWhiteSpace(verdi)) return false;
        var mål = (r.Verdi ?? "").Trim();

        switch (r.Operator)
        {
            case "=": return string.Equals(verdi, mål, StringComparison.OrdinalIgnoreCase);
            case "≠": return !string.Equals(verdi, mål, StringComparison.OrdinalIgnoreCase);
            case "inneholder": return verdi.Contains(mål, StringComparison.OrdinalIgnoreCase);
            case "starter med": return verdi.StartsWith(mål, StringComparison.OrdinalIgnoreCase);
            case "er en av":
                return mål.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Any(x => string.Equals(x, verdi, StringComparison.OrdinalIgnoreCase));
            case ">":
            case "<":
            case "mellom":
                if (!TalltolkNorsk(verdi, out var v)) return false;
                if (r.Operator == "mellom")
                {
                    var deler = mål.Split('-', 2);
                    return deler.Length == 2 && TalltolkNorsk(deler[0], out var lo) && TalltolkNorsk(deler[1], out var hi) && v >= lo && v <= hi;
                }
                if (!TalltolkNorsk(mål, out var m)) return false;
                return r.Operator == ">" ? v > m : v < m;
            default: return false;
        }
    }

    // Kundens verdi for en feltkatalog-nøkkel (kun felt som finnes på kundekortet).
    private static string? KundeVerdi(Kundekort k, string key) => key switch
    {
        "postnummer" => k.Postnummer,
        "poststed" => k.Poststed,
        "kommune" => k.Kommune,
        "fylke" => k.Fylke,
        "laanetype" => k.Laanetype,
        "laanebelop" => k.OnsketLaanebelop?.ToString(Inv),
        "lopetid" => k.OnsketLopetidMnd?.ToString(Inv),
        "formaal" => k.Laaneformal,
        "aarsinntekt" => k.AarsinntektBrutto?.ToString(Inv),
        "sivilstatus" => k.Sivilstatus,
        "ansettelse" => k.Arbeidssituasjon,
        "boligstatus" => Boligstatus(k.Boforhold),
        "naavaerende_bank" => k.NavarendeBank,
        _ => null,
    };

    private static string? Boligstatus(string? boforhold)
    {
        var x = (boforhold ?? "").ToLowerInvariant();
        if (x.Length == 0) return null;
        if (x.Contains("eier") || x.Contains("selveier") || x.Contains("andel") || x.Contains("borettslag")) return "Eier";
        if (x.Contains("leier") || x.Contains("foreldre")) return "Leier";
        return null;
    }

    private static bool TalltolkNorsk(string s, out decimal d)
    {
        var clean = new string((s ?? "").Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (clean.Contains(',') && !clean.Contains('.')) clean = clean.Replace(',', '.');
        else clean = clean.Replace(",", "");
        return decimal.TryParse(clean, NumberStyles.Any, Inv, out d);
    }
}
