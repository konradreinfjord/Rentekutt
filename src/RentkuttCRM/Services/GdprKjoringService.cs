using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>Én kjøring av en GDPR-jobb (anonymisering/sletting/reparasjon). Rad per forsøk.</summary>
[Table("gdpr_jobb_kjoring")]
public class GdprJobbKjoring : BaseModel
{
    [PrimaryKey("id", false)] public long Id { get; set; }
    [Column("jobb")] public string Jobb { get; set; } = "";
    [Column("startet_at", ignoreOnInsert: true)] public DateTime StartetAt { get; set; }
    [Column("fullfort_at")] public DateTime? FullfortAt { get; set; }
    [Column("antall_behandlet")] public int? AntallBehandlet { get; set; }
    [Column("feilmelding")] public string? Feilmelding { get; set; }
}

/// <summary>Skriver/leser kjøringsloggen for GDPR-jobbene. Bruker Supabase-API (service_role), som
/// virker uavhengig av jobbenes egen direkte-Postgres-tilkobling — slik at Alarm 1 kan evalueres
/// selv når sletterutinen er «død» på sin egen kanal.</summary>
public class GdprKjoringService
{
    public const string JobbAnonymisering = "anonymisering";
    public const string JobbSletting = "sletting";
    public const string JobbReparasjon = "reparasjon";

    private readonly Supabase.Client _client;
    private readonly ILogger<GdprKjoringService> _log;
    public bool IsConfigured { get; }
    private static readonly List<GdprJobbKjoring> _staging = new();
    private static long _stagingId = 1;

    public GdprKjoringService(Supabase.Client client, IConfiguration cfg, ILogger<GdprKjoringService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Opprett en forsøk-rad (fullfort_at = null). Returnerer id, eller null hvis lagring feilet.</summary>
    public async Task<long?> StartAsync(string jobb)
    {
        if (!IsConfigured) { var r = new GdprJobbKjoring { Id = _stagingId++, Jobb = jobb, StartetAt = DateTime.UtcNow }; _staging.Insert(0, r); return r.Id; }
        try
        {
            var lagret = (await _client.From<GdprJobbKjoring>().Insert(new GdprJobbKjoring { Jobb = jobb })).Models.FirstOrDefault();
            return lagret?.Id;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke starte GDPR-kjøringslogg ({Jobb})", jobb); return null; }
    }

    public async Task FullfortAsync(long id, int antall)
    {
        if (!IsConfigured) { var r = _staging.FirstOrDefault(x => x.Id == id); if (r is not null) { r.FullfortAt = DateTime.UtcNow; r.AntallBehandlet = antall; } return; }
        try
        {
            await _client.From<GdprJobbKjoring>().Where(x => x.Id == id)
                .Set(x => x.FullfortAt!, DateTime.UtcNow).Set(x => x.AntallBehandlet!, antall).Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke markere GDPR-kjøring fullført ({Id})", id); }
    }

    public async Task FeiletAsync(long id, string feilmelding)
    {
        if (!IsConfigured) { var r = _staging.FirstOrDefault(x => x.Id == id); if (r is not null) r.Feilmelding = feilmelding; return; }
        try
        {
            await _client.From<GdprJobbKjoring>().Where(x => x.Id == id)
                .Set(x => x.Feilmelding!, feilmelding.Length > 500 ? feilmelding[..500] : feilmelding).Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke markere GDPR-kjøring feilet ({Id})", id); }
    }

    /// <summary>Tidspunkt for siste FULLFØRTE kjøring av gitt jobbtype, eller null hvis ingen finnes.</summary>
    public async Task<DateTime?> SisteFullfortAsync(string jobb)
    {
        if (!IsConfigured)
            return _staging.Where(x => x.Jobb == jobb && x.FullfortAt is not null).Max(x => (DateTime?)x.FullfortAt);
        try
        {
            // NB: IKKE bruk «x.FullfortAt != null» i Where — PostgREST oversetter det til «neq.null»,
            // som gir HTTP 400 (ugyldig null-sammenligning) → spørringen feiler → falsk «aldri fullført».
            // Vi henter siste kjøringer for jobben og finner nyeste fullførte klient-side.
            var rader = (await _client.From<GdprJobbKjoring>()
                .Where(x => x.Jobb == jobb)
                .Order(x => x.FullfortAt!, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(100).Get()).Models;
            return rader.Count == 0 ? null : rader.Max(x => x.FullfortAt);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Oppslag av siste fullførte GDPR-kjøring feilet ({Jobb})", jobb); return null; }
    }
}
