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

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
