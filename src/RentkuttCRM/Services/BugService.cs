using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>Bug/melding i det interne sporet mellom kundeservice og admin. Én tabell, ingen relasjoner.</summary>
[Table("bug")]
public class Bug : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kategori")] public string? Kategori { get; set; }
    [Column("beskrivelse")] public string Beskrivelse { get; set; } = "";
    [Column("status")] public string Status { get; set; } = BugService.StatusIkkeRegistrert;
    [Column("opprettet_av")] public string? OpprettetAv { get; set; }
    [Column("opprettet_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime OpprettetAt { get; set; }
    [Column("oppdatert_at")] public DateTime OppdatertAt { get; set; }
    [Column("teknisk_kommentar")] public string? TekniskKommentar { get; set; }
    [Column("info_fra_ks")] public string? InfoFraKs { get; set; }
}

/// <summary>Bildevedlegg til én bug-sak (én rad per bug). Bildet ligger som data:-URL i <see cref="Data"/>.</summary>
[Table("bug_bilde")]
public class BugBilde : BaseModel
{
    [PrimaryKey("bug_id", true)] public Guid BugId { get; set; }
    [Column("data")] public string Data { get; set; } = "";
    [Column("navn")] public string? Navn { get; set; }
    [Column("type")] public string? Type { get; set; }
    [Column("opprettet_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime OpprettetAt { get; set; }
}

/// <summary>Tjenestelag for Bugs-fanen. Ingen forretningslogikk utover trimming og null-håndtering.</summary>
public class BugService
{
    public const string StatusIkkeRegistrert = "ikke_registrert";
    public const string StatusPaagaar = "pagaar";
    public const string StatusUtfort = "utfort";
    public const string StatusUtfortIkkeLost = "utfort_ikke_lost";
    public const string StatusUtfortArkivert = "utfort_arkivert";

    /// <summary>Statuskode → etikett (koden lagres, etiketten vises).</summary>
    public static readonly (string Kode, string Etikett)[] Statuser =
    {
        (StatusIkkeRegistrert, "Ikke registrert"),
        (StatusPaagaar, "Pågår"),
        (StatusUtfort, "Utført"),
        (StatusUtfortIkkeLost, "Utført ikke løst"),
        (StatusUtfortArkivert, "Utført arkivert"),
    };

    public static readonly string[] Kategorier =
        { "Feil / bug", "Forbedring", "Design / UX", "Data / tall", "Ytelse", "Ny funksjon", "Annet" };

    public static string Etikett(string? kode) => Statuser.FirstOrDefault(s => s.Kode == kode).Etikett ?? (kode ?? "—");
    public static string Farge(string? kode) => kode switch
    {
        StatusIkkeRegistrert => "#9aa0a8",     // grå
        StatusPaagaar => "#c2740a",            // gul/amber
        StatusUtfort => "#2f8a3e",             // grønn
        StatusUtfortIkkeLost => "#b42318",     // rød
        StatusUtfortArkivert => "#5b6b7f",     // grå-blå
        _ => "#9aa0a8",
    };

    private readonly Supabase.Client _client;
    private readonly ILogger<BugService> _log;
    public bool IsConfigured { get; }
    private static readonly List<Bug> _staging = new();
    private static readonly Dictionary<Guid, BugBilde> _bildeStaging = new();

    public BugService(Supabase.Client client, IConfiguration cfg, ILogger<BugService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    // Sortering (pkt. 6): ubehandlede først, så pågående, så resten — nyeste først innen hver gruppe.
    private static int SortRang(string? status) => status switch
    {
        StatusIkkeRegistrert => 0,
        StatusPaagaar => 1,
        _ => 2,
    };

    public async Task<List<Bug>> GetBugsAsync()
    {
        List<Bug> alle;
        if (!IsConfigured) alle = _staging.ToList();
        else
        {
            try { alle = (await _client.From<Bug>().Get()).Models; }
            catch (Exception ex) { _log.LogWarning(ex, "Henting av bugs feilet"); return new(); }
        }
        return alle.OrderBy(b => SortRang(b.Status)).ThenByDescending(b => b.OpprettetAt).ToList();
    }

    /// <summary>Lett oppslag: id-ene til saker som har et bildevedlegg (henter kun nøkkelen, ikke selve
    /// bildet, så bug-lista holder seg lett). Tåler at bug_bilde-tabellen ennå ikke finnes.</summary>
    public async Task<HashSet<Guid>> HentBildeIderAsync()
    {
        if (!IsConfigured) return _bildeStaging.Keys.ToHashSet();
        try
        {
            return (await _client.From<BugBilde>().Select("bug_id").Get()).Models.Select(x => x.BugId).ToHashSet();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Oppslag av bilde-id-er feilet"); return new(); }
    }

    /// <summary>Lagrer (eller erstatter) bildevedlegget for en sak. <paramref name="dataUrl"/> er en data:-URL.</summary>
    public async Task LastOppBildeAsync(Guid bugId, string dataUrl, string? navn, string? type)
    {
        if (!IsConfigured)
        {
            _bildeStaging[bugId] = new BugBilde { BugId = bugId, Data = dataUrl, Navn = navn, Type = type, OpprettetAt = DateTime.UtcNow };
            return;
        }
        try
        {
            // Én rad per bug (PK = bug_id): fjern ev. eksisterende og sett inn på nytt.
            await _client.From<BugBilde>().Where(x => x.BugId == bugId).Delete();
            await _client.From<BugBilde>().Insert(new BugBilde { BugId = bugId, Data = dataUrl, Navn = navn, Type = type });
        }
        catch (Exception ex) { _log.LogError(ex, "Opplasting av bug-bilde feilet"); }
    }

    public async Task<BugBilde?> HentBildeAsync(Guid bugId)
    {
        if (!IsConfigured) return _bildeStaging.TryGetValue(bugId, out var s) ? s : null;
        try { return (await _client.From<BugBilde>().Where(x => x.BugId == bugId).Get()).Models.FirstOrDefault(); }
        catch (Exception ex) { _log.LogError(ex, "Henting av bug-bilde feilet"); return null; }
    }

    public async Task SlettBildeAsync(Guid bugId)
    {
        if (!IsConfigured) { _bildeStaging.Remove(bugId); return; }
        try { await _client.From<BugBilde>().Where(x => x.BugId == bugId).Delete(); }
        catch (Exception ex) { _log.LogError(ex, "Sletting av bug-bilde feilet"); }
    }

    public async Task<Bug?> OpprettBugAsync(string? kategori, string beskrivelse, string? opprettetAv)
    {
        var b = new Bug
        {
            Kategori = string.IsNullOrWhiteSpace(kategori) ? Kategorier[0] : kategori.Trim(),
            Beskrivelse = (beskrivelse ?? "").Trim(),
            Status = StatusIkkeRegistrert,
            OpprettetAv = opprettetAv,
            OppdatertAt = DateTime.UtcNow,
        };
        if (!IsConfigured) { b.Id = Guid.NewGuid(); b.OpprettetAt = DateTime.UtcNow; _staging.Insert(0, b); return b; }
        try { return (await _client.From<Bug>().Insert(b)).Models.FirstOrDefault() ?? b; }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av bug feilet"); return null; }
    }

    public async Task OppdaterBugStatusAsync(Guid id, string status)
    {
        if (!IsConfigured) { var b = _staging.FirstOrDefault(x => x.Id == id); if (b is not null) { b.Status = status; b.OppdatertAt = DateTime.UtcNow; } return; }
        try { await _client.From<Bug>().Where(x => x.Id == id).Set(x => x.Status, status).Set(x => x.OppdatertAt, DateTime.UtcNow).Update(); }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av bug-status feilet"); }
    }

    public async Task OppdaterBugTekniskKommentarAsync(Guid id, string? verdi)
    {
        var v = string.IsNullOrWhiteSpace(verdi) ? null : verdi.Trim();
        if (!IsConfigured) { var b = _staging.FirstOrDefault(x => x.Id == id); if (b is not null) { b.TekniskKommentar = v; b.OppdatertAt = DateTime.UtcNow; } return; }
        try { await _client.From<Bug>().Where(x => x.Id == id).Set(x => x.TekniskKommentar!, v).Set(x => x.OppdatertAt, DateTime.UtcNow).Update(); }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av teknisk kommentar feilet"); }
    }

    public async Task OppdaterBugInfoFraKsAsync(Guid id, string? verdi)
    {
        var v = string.IsNullOrWhiteSpace(verdi) ? null : verdi.Trim();
        if (!IsConfigured) { var b = _staging.FirstOrDefault(x => x.Id == id); if (b is not null) { b.InfoFraKs = v; b.OppdatertAt = DateTime.UtcNow; } return; }
        try { await _client.From<Bug>().Where(x => x.Id == id).Set(x => x.InfoFraKs!, v).Set(x => x.OppdatertAt, DateTime.UtcNow).Update(); }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av info fra KS feilet"); }
    }

    public async Task SlettBugAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); _bildeStaging.Remove(id); return; }
        // I DB rydder «on delete cascade» bug_bilde automatisk.
        try { await _client.From<Bug>().Where(x => x.Id == id).Delete(); }
        catch (Exception ex) { _log.LogError(ex, "Sletting av bug feilet"); }
    }
}
