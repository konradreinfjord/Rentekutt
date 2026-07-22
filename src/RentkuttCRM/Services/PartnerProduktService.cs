using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("partner_produkt")]
public class PartnerProdukt : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("partner_id")] public Guid PartnerId { get; set; }
    [Column("navn")] public string Navn { get; set; } = "";
    [Column("kode")] public int? Kode { get; set; }                 // Instabank API-kode; null for manuelle banker
    [Column("segment")] public string Segment { get; set; } = "privat"; // 'privat' | 'bedrift'
    [Column("provisjon")] public string? Provisjon { get; set; }
    [Column("engangssum")] public string? Engangssum { get; set; }
    // Lånetyper produktet gjelder for (komma-separert) — driver auto-valg av produkt
    // ut fra kundens lånetype ved sending. Tom = ikke knyttet til noen bestemt lånetype.
    [Column("laanetyper")] public string? Laanetyper { get; set; }
    [Column("aktiv")] public bool Aktiv { get; set; } = true;
    [Column("sortering")] public int Sortering { get; set; }
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; }

    public bool ErBedrift => string.Equals(Segment, "bedrift", StringComparison.OrdinalIgnoreCase);

    public List<string> LaanetyperListe =>
        (Laanetyper ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>True hvis produktet er eksplisitt knyttet til den gitte lånetypen.</summary>
    public bool GjelderLaanetype(string? laanetype)
    {
        var liste = LaanetyperListe;
        return liste.Count > 0
            && !string.IsNullOrWhiteSpace(laanetype)
            && liste.Any(l => string.Equals(l, laanetype, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Segmentet som matcher en kundetype ("B2B" → bedrift, ellers privat).</summary>
    public static string SegmentFor(string? kundeType) =>
        string.Equals(kundeType, "B2B", StringComparison.OrdinalIgnoreCase) ? "bedrift" : "privat";
}

/// <summary>Produkter per bankpartner (med staging-fallback når Supabase ikke er konfigurert).</summary>
public class PartnerProduktService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<PartnerProduktService> _log;
    public bool IsConfigured { get; }

    private static readonly List<PartnerProdukt> _staging = new();
    private bool _initialized;

    public PartnerProduktService(Supabase.Client client, IConfiguration cfg, ILogger<PartnerProduktService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<PartnerProdukt>> ListAsync()
    {
        if (!IsConfigured) return _staging.OrderBy(p => p.Sortering).ThenBy(p => p.Navn).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<PartnerProdukt>()
                .Order(x => x.Sortering, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av partnerprodukter feilet"); return new(); }
    }

    public async Task<(PartnerProdukt? Produkt, string? Feil)> AddAsync(Guid partnerId, string navn, string segment,
        int? kode = null, string? provisjon = null, string? engangssum = null, string? laanetyper = null)
    {
        navn = (navn ?? "").Trim();
        if (string.IsNullOrWhiteSpace(navn)) return (null, "Produktnavn er påkrevd.");
        segment = string.Equals(segment, "bedrift", StringComparison.OrdinalIgnoreCase) ? "bedrift" : "privat";
        var p = new PartnerProdukt
        {
            PartnerId = partnerId,
            Navn = navn,
            Kode = kode,
            Segment = segment,
            Provisjon = provisjon,
            Engangssum = engangssum,
            Laanetyper = string.IsNullOrWhiteSpace(laanetyper) ? null : laanetyper.Trim(),
            Aktiv = true,
        };
        if (!IsConfigured) { p.Id = Guid.NewGuid(); _staging.Add(p); return (p, null); }
        try
        {
            await EnsureInitAsync();
            var lagret = (await _client.From<PartnerProdukt>().Insert(p)).Models.FirstOrDefault();
            return (lagret, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av produkt feilet"); return (null, ex.Message); }
    }

    /// <summary>Oppdater kjernefeltene (navn, segment, kode) på et produkt.</summary>
    public async Task UpdateAsync(PartnerProdukt p)
    {
        p.Navn = (p.Navn ?? "").Trim();
        p.Segment = string.Equals(p.Segment, "bedrift", StringComparison.OrdinalIgnoreCase) ? "bedrift" : "privat";
        if (!IsConfigured) return;   // staging holder allerede referansen
        try
        {
            await EnsureInitAsync();
            await _client.From<PartnerProdukt>().Update(p);
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av produkt feilet"); }
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
            await _client.From<PartnerProdukt>().Where(x => x.Id == id)
                .Set(x => x.Provisjon!, provisjon ?? "")
                .Set(x => x.Engangssum!, engangssum ?? "")
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av produkt-kompensasjon feilet"); }
    }

    public async Task SetAktivAsync(Guid id, bool aktiv)
    {
        if (!IsConfigured) { var p = _staging.FirstOrDefault(x => x.Id == id); if (p is not null) p.Aktiv = aktiv; return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<PartnerProdukt>().Where(x => x.Id == id).Set(x => x.Aktiv, aktiv).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av produkt (aktiv) feilet"); }
    }

    public async Task UpdateLaanetyperAsync(Guid id, string? laanetyper)
    {
        laanetyper = string.IsNullOrWhiteSpace(laanetyper) ? null : laanetyper.Trim();
        if (!IsConfigured) { var p = _staging.FirstOrDefault(x => x.Id == id); if (p is not null) p.Laanetyper = laanetyper; return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<PartnerProdukt>().Where(x => x.Id == id).Set(x => x.Laanetyper!, laanetyper ?? "").Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av produkt (lånetyper) feilet"); }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return; }
        try
        {
            await EnsureInitAsync();
            await _client.From<PartnerProdukt>().Where(x => x.Id == id).Delete();
        }
        catch (Exception ex) { _log.LogError(ex, "Sletting av produkt feilet"); }
    }

    /// <summary>Selvhelbredende: sørg for at en Instabank-partner har alle standardproduktene.
    /// Idempotent — legger kun til produktkoder som mangler. Returnerer antall lagt til.</summary>
    public async Task<int> EnsureInstabankStandardAsync(Guid partnerId, IEnumerable<PartnerProdukt> eksisterende)
    {
        var koder = eksisterende
            .Where(p => p.PartnerId == partnerId && p.Kode is not null)
            .Select(p => p.Kode!.Value).ToHashSet();
        var lagtTil = 0;
        foreach (var sp in InstabankService.StandardProdukter)
        {
            if (koder.Contains(sp.Kode)) continue;
            var (p, _) = await AddAsync(partnerId, sp.Navn, sp.Segment, sp.Kode, null, null,
                string.IsNullOrEmpty(sp.Laanetyper) ? null : sp.Laanetyper);
            if (p is not null) lagtTil++;
        }
        return lagtTil;
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
