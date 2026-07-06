using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>
/// Ett tidsstemplet notat på et kundekort. Bygger en kronologisk logg over alt
/// som er gjort med kunden (samtaler, avtaler, oppfølging).
/// </summary>
[Table("saksnotat")]
public class Saksnotat : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("tekst")] public string Tekst { get; set; } = "";
    [Column("forfatter")] public string? Forfatter { get; set; }
    [Column("forfatter_navn")] public string? ForfatterNavn { get; set; }
    [Column("opprettet", ignoreOnInsert: true)] public DateTime Opprettet { get; set; }
}

/// <summary>Tidsstemplede saksnotater (med staging-fallback når Supabase ikke er konfigurert).</summary>
public class NotatService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<NotatService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Saksnotat> _staging = new();
    private bool _initialized;

    public NotatService(Supabase.Client client, IConfiguration cfg, ILogger<NotatService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Notater for ett kundekort, nyeste først.</summary>
    public async Task<List<Saksnotat>> ListAsync(Guid kundekortId)
    {
        if (!IsConfigured)
            return _staging.Where(n => n.KundekortId == kundekortId)
                .OrderByDescending(n => n.Opprettet).ToList();
        try
        {
            await EnsureReadyAsync();
            return (await _client.From<Saksnotat>()
                .Where(n => n.KundekortId == kundekortId)
                .Order(n => n.Opprettet, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av saksnotater feilet");
            return new();
        }
    }

    /// <summary>Legg til et notat med automatisk tidsstempel (settes av DB / nå).</summary>
    public async Task<bool> AddAsync(Guid kundekortId, string tekst, string? forfatter, string? forfatterNavn)
    {
        tekst = (tekst ?? "").Trim();
        if (kundekortId == Guid.Empty || string.IsNullOrWhiteSpace(tekst)) return false;

        if (!IsConfigured)
        {
            _staging.Add(new Saksnotat
            {
                Id = Guid.NewGuid(), KundekortId = kundekortId, Tekst = tekst,
                Forfatter = forfatter, ForfatterNavn = forfatterNavn, Opprettet = DateTime.UtcNow,
            });
            return true;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Saksnotat>().Insert(new Saksnotat
            {
                KundekortId = kundekortId, Tekst = tekst,
                Forfatter = forfatter, ForfatterNavn = forfatterNavn,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lagring av saksnotat feilet");
            return false;
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
