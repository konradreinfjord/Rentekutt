using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("kundekort_logg")]
public class KundekortLogg : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("aktor")] public string? Aktor { get; set; }
    [Column("tekst")] public string Tekst { get; set; } = "";
    [Column("kategori")] public string Kategori { get; set; } = "endring";
    [Column("begrunnelse")] public string? Begrunnelse { get; set; }
    [Column("opprettet", ignoreOnInsert: true)] public DateTime Opprettet { get; set; }
}

/// <summary>Endringslogg per kundekort (audit trail). Feiler aldri hardt —
/// logging skal ikke kunne velte selve lagringen.</summary>
public class LoggService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<LoggService> _log;
    public bool IsConfigured { get; }

    private static readonly List<KundekortLogg> _staging = new();
    private bool _initialized;

    public LoggService(Supabase.Client client, IConfiguration cfg, ILogger<LoggService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public Task LoggAsync(Guid kundekortId, string? aktor, string tekst, string kategori = "endring", string? begrunnelse = null)
        => LoggFlereAsync(kundekortId, aktor, new[] { tekst }, kategori, begrunnelse);

    /// <summary>Loggfør innsyn/lesing av et kundekort (art. 15 / accountability).</summary>
    public Task LoggInnsynAsync(Guid kundekortId, string? aktor, string tekst, string? begrunnelse = null)
        => LoggAsync(kundekortId, aktor, tekst, "innsyn", begrunnelse);

    /// <summary>Skriv flere logglinjer på én gang (f.eks. flere feltendringer i én lagring).</summary>
    public async Task LoggFlereAsync(Guid kundekortId, string? aktor, IEnumerable<string> tekster, string kategori = "endring", string? begrunnelse = null)
    {
        var rader = tekster
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new KundekortLogg { KundekortId = kundekortId, Aktor = aktor, Tekst = t, Kategori = kategori, Begrunnelse = begrunnelse })
            .ToList();
        if (rader.Count == 0) return;

        if (!IsConfigured)
        {
            var naa = DateTime.UtcNow;
            foreach (var r in rader) { r.Id = Guid.NewGuid(); r.Opprettet = naa; _staging.Insert(0, r); }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<KundekortLogg>().Insert(rader);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Skriving av kundekort-logg feilet"); }
    }

    public async Task<List<KundekortLogg>> ForKundeAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _staging.Where(x => x.KundekortId == kundekortId).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<KundekortLogg>()
                .Where(x => x.KundekortId == kundekortId)
                .Order(x => x.Opprettet, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av kundekort-logg feilet"); return new(); }
    }

    public record StatusHendelse(Guid KundekortId, string Status, DateTime Opprettet);

    /// <summary>Alle statusendringer (fra endringsloggen) etter et tidspunkt — brukes til syklustider.
    /// Leser logglinjer «Endret status: X → Y» og «Endret status til Y», paginert for å unngå rad-tak.</summary>
    public async Task<List<StatusHendelse>> StatusHendelserSidenAsync(DateTime fraUtc)
    {
        var res = new List<StatusHendelse>();
        void LeggTil(IEnumerable<KundekortLogg> rader)
        {
            foreach (var r in rader)
            {
                var s = ParseStatus(r.Tekst);
                if (s is not null) res.Add(new StatusHendelse(r.KundekortId, s, r.Opprettet));
            }
        }

        if (!IsConfigured)
        {
            LeggTil(_staging.Where(x => x.Kategori == "endring" && x.Opprettet >= fraUtc));
            return res;
        }
        try
        {
            await EnsureInitAsync();
            const int side = 1000;
            for (var from = 0; from <= 200000; from += side)
            {
                var batch = (await _client.From<KundekortLogg>()
                    .Where(x => x.Kategori == "endring")
                    .Filter("opprettet", Constants.Operator.GreaterThanOrEqual, fraUtc.ToUniversalTime().ToString("o"))
                    .Order(x => x.Opprettet, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                    .Range(from, from + side - 1)
                    .Get()).Models;
                LeggTil(batch);
                if (batch.Count < side) break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av statushendelser feilet"); }
        return res;
    }

    /// <summary>Alle logglinjer i en kategori etter et tidspunkt (paginert). Brukes til statistikk,
    /// f.eks. arbeidsoppgaver kvittert ut (kategori «arbeidsoppgave»).</summary>
    public async Task<List<KundekortLogg>> KategoriSidenAsync(string kategori, DateTime fraUtc)
    {
        if (!IsConfigured)
            return _staging.Where(x => x.Kategori == kategori && x.Opprettet >= fraUtc).ToList();
        var res = new List<KundekortLogg>();
        try
        {
            await EnsureInitAsync();
            const int side = 1000;
            for (var from = 0; from <= 200000; from += side)
            {
                var batch = (await _client.From<KundekortLogg>()
                    .Where(x => x.Kategori == kategori)
                    .Filter("opprettet", Constants.Operator.GreaterThanOrEqual, fraUtc.ToUniversalTime().ToString("o"))
                    .Order(x => x.Opprettet, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                    .Range(from, from + side - 1)
                    .Get()).Models;
                res.AddRange(batch);
                if (batch.Count < side) break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av logg ({Kategori}) feilet", kategori); }
        return res;
    }

    /// <summary>Alle handlinger (logglinjer) etter et tidspunkt UNNTATT innsyn/lesing — brukes til
    /// å måle reell agent-aktivitet (hvem gjorde noe med en sak, når). Paginert.</summary>
    public async Task<List<KundekortLogg>> HandlingerSidenAsync(DateTime fraUtc)
    {
        if (!IsConfigured)
            return _staging.Where(x => x.Opprettet >= fraUtc && x.Kategori != "innsyn").ToList();
        var res = new List<KundekortLogg>();
        try
        {
            await EnsureInitAsync();
            const int side = 1000;
            for (var from = 0; from <= 500000; from += side)
            {
                var batch = (await _client.From<KundekortLogg>()
                    .Filter("opprettet", Constants.Operator.GreaterThanOrEqual, fraUtc.ToUniversalTime().ToString("o"))
                    .Order(x => x.Opprettet, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                    .Range(from, from + side - 1)
                    .Get()).Models;
                res.AddRange(batch.Where(x => x.Kategori != "innsyn"));
                if (batch.Count < side) break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av handlinger feilet"); }
        return res;
    }

    private static string? ParseStatus(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst) || !tekst.StartsWith("Endret status", StringComparison.OrdinalIgnoreCase)) return null;
        var pil = tekst.LastIndexOf('→');
        if (pil >= 0) return tekst[(pil + 1)..].Trim();
        var idx = tekst.IndexOf(" til ", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? tekst[(idx + 5)..].Trim() : null;
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
