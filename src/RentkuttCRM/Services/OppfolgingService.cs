using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>En oppfølging (pipeline-oppgave) på en søknad: forfallsdato + notat, eid av en rådgiver.
/// Flere per søknad. Åpne oppfølginger driver «Oppfølgingsdato» på kundekortet (neste_oppfolging).</summary>
[Table("oppfolging")]
public class OppfolgingOppgave : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("eier")] public string? Eier { get; set; }
    [Column("forfaller")] public DateTime Forfaller { get; set; }
    [Column("notat")] public string? Notat { get; set; }
    [Column("fullfort")] public bool Fullfort { get; set; }
    [Column("fullfort_at")] public DateTime? FullfortAt { get; set; }
    [Column("opprettet_av")] public string? OpprettetAv { get; set; }
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; }
}

/// <summary>Oppfølgings-pipeline: opprett, fullfør og slett oppfølginger på søknader. Holder
/// kundekortets neste_oppfolging synkronisert med tidligste åpne oppfølging (til visning i tabeller).</summary>
public class OppfolgingService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<OppfolgingService> _log;
    public bool IsConfigured { get; }

    private static readonly List<OppfolgingOppgave> _staging = new();

    public OppfolgingService(Supabase.Client client, IConfiguration cfg, ILogger<OppfolgingService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Alle oppfølginger for én søknad (åpne først, deretter fullførte), sortert på forfall.</summary>
    public async Task<List<OppfolgingOppgave>> ForKundekortAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _staging.Where(x => x.KundekortId == kundekortId).OrderBy(SortNokkel).ToList();
        try
        {
            var rader = (await _client.From<OppfolgingOppgave>().Where(x => x.KundekortId == kundekortId).Get()).Models;
            return rader.OrderBy(SortNokkel).ToList();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av oppfølginger (kort) feilet"); return new(); }
    }

    /// <summary>Åpne oppfølginger for en rådgiver (på tvers av søknader), tidligste forfall først.</summary>
    public async Task<List<OppfolgingOppgave>> AapneForEierAsync(string eier)
    {
        if (string.IsNullOrWhiteSpace(eier)) return new();
        if (!IsConfigured) return _staging.Where(x => x.Eier == eier && !x.Fullfort).OrderBy(x => x.Forfaller).ToList();
        try
        {
            // Filtrer bool i minnet (PostgREST-klienten tåler ikke bool == false godt i uttrykkstreet).
            var rader = (await _client.From<OppfolgingOppgave>().Where(x => x.Eier == eier).Get()).Models;
            return rader.Where(x => !x.Fullfort).OrderBy(x => x.Forfaller).ToList();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av åpne oppfølginger (eier) feilet"); return new(); }
    }

    public async Task<OppfolgingOppgave?> OpprettAsync(Guid kundekortId, string? eier, DateTime forfaller, string? notat, string? opprettetAv)
    {
        var o = new OppfolgingOppgave
        {
            KundekortId = kundekortId, Eier = eier, Forfaller = forfaller.Date,
            Notat = notat, Fullfort = false, OpprettetAv = opprettetAv,
        };
        if (!IsConfigured) { o.Id = Guid.NewGuid(); o.CreatedAt = DateTime.UtcNow; _staging.Insert(0, o); return o; }
        try
        {
            var resp = await _client.From<OppfolgingOppgave>().Insert(o);
            var lagret = resp.Models.FirstOrDefault() ?? o;
            await OppdaterNesteAsync(kundekortId);
            return lagret;
        }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av oppfølging feilet"); return null; }
    }

    public async Task FullforAsync(Guid id, Guid kundekortId)
    {
        if (!IsConfigured)
        {
            var o = _staging.FirstOrDefault(x => x.Id == id);
            if (o is not null) { o.Fullfort = true; o.FullfortAt = DateTime.UtcNow; }
            return;
        }
        try
        {
            await _client.From<OppfolgingOppgave>().Where(x => x.Id == id)
                .Set(x => x.Fullfort, true)
                .Set(x => x.FullfortAt!, DateTime.UtcNow)
                .Update();
            await OppdaterNesteAsync(kundekortId);
        }
        catch (Exception ex) { _log.LogError(ex, "Fullføring av oppfølging feilet"); }
    }

    public async Task SlettAsync(Guid id, Guid kundekortId)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return; }
        try
        {
            await _client.From<OppfolgingOppgave>().Where(x => x.Id == id).Delete();
            await OppdaterNesteAsync(kundekortId);
        }
        catch (Exception ex) { _log.LogError(ex, "Sletting av oppfølging feilet"); }
    }

    /// <summary>Synk kundekort.neste_oppfolging = tidligste åpne oppfølging (eller null). Gjør at
    /// «Oppfølgingsdato»-kolonnen i søknadstabellene kan vises uten ekstra spørring per rad.</summary>
    private async Task OppdaterNesteAsync(Guid kundekortId)
    {
        try
        {
            var aapne = (await _client.From<OppfolgingOppgave>().Where(x => x.KundekortId == kundekortId).Get())
                .Models.Where(x => !x.Fullfort).ToList();
            DateTime? neste = aapne.Count == 0 ? null : aapne.Min(x => x.Forfaller);
            await _client.From<Kundekort>().Where(x => x.Id == kundekortId)
                .Set(x => x.NesteOppfolging!, neste).Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Synk av neste_oppfolging feilet"); }
    }

    // Åpne før fullførte, deretter etter forfall.
    private static (bool, DateTime) SortNokkel(OppfolgingOppgave o) => (o.Fullfort, o.Forfaller);
}
