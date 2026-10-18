using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>Én innkommende SMS fra en kunde (MO — mobile originated), koblet til kundekort på mobilnummer.</summary>
[Table("innkommende_sms")]
public class InnkommendeSms : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid? KundekortId { get; set; }
    [Column("fra_nummer")] public string FraNummer { get; set; } = "";
    [Column("til_nummer")] public string? TilNummer { get; set; }
    [Column("tekst")] public string Tekst { get; set; } = "";
    [Column("raw")] public string? Raw { get; set; }
    [Column("mottatt_at", ignoreOnInsert: true)] public DateTime MottattAt { get; set; }
}

/// <summary>
/// Lagrer og henter innkommende SMS (kundesvar) fra LinkMobility MO-webhook. Kobler på mobilnummer
/// til riktig kundekort og logger på saken. Fungerer både mot Supabase og i minnet (staging/dev-test).
/// </summary>
public class InnkommendeSmsService
{
    private readonly Supabase.Client _client;
    private readonly KundekortService _kundekort;
    private readonly LoggService _logg;
    private readonly ILogger<InnkommendeSmsService> _log;
    public bool IsConfigured { get; }
    private static readonly List<InnkommendeSms> _staging = new();

    public InnkommendeSmsService(Supabase.Client client, KundekortService kundekort, LoggService logg,
        IConfiguration cfg, ILogger<InnkommendeSmsService> log)
    {
        _client = client;
        _kundekort = kundekort;
        _logg = logg;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Tar imot en innkommende melding: kobler til kundekort på mobil, lagrer og logger på saken.</summary>
    public async Task<InnkommendeSms> MottaAsync(string fraNummer, string? tilNummer, string tekst, string? raw)
    {
        var kort = await _kundekort.FindByMobilAsync(fraNummer);
        var rad = new InnkommendeSms
        {
            KundekortId = kort?.Id,
            FraNummer = fraNummer ?? "",
            TilNummer = tilNummer,
            Tekst = tekst ?? "",
            Raw = raw,
        };

        if (!IsConfigured) { rad.Id = Guid.NewGuid(); rad.MottattAt = DateTime.UtcNow; _staging.Insert(0, rad); }
        else
        {
            try { rad = (await _client.From<InnkommendeSms>().Insert(rad)).Models.FirstOrDefault() ?? rad; }
            catch (Exception ex) { _log.LogError(ex, "Lagring av innkommende SMS feilet"); }
        }

        // Logg på kundekortet så svaret vises i sakshistorikken.
        if (kort is not null)
        {
            var kort140 = (tekst ?? "").Length > 140 ? tekst![..140] + "…" : tekst ?? "";
            try { await _logg.LoggAsync(kort.Id, $"Kunde ({fraNummer})", $"Innkommende SMS: «{kort140}»", "sms"); }
            catch (Exception ex) { _log.LogWarning(ex, "Logging av innkommende SMS på sak feilet"); }
        }
        else
            _log.LogInformation("Innkommende SMS fra {Fra} fant ingen matchende sak.", fraNummer);

        return rad;
    }

    /// <summary>Siste innkommende SMS (nyeste først) — for inbox/test-visning i Admin.</summary>
    public async Task<List<InnkommendeSms>> SisteAsync(int antall = 50)
    {
        if (!IsConfigured) return _staging.OrderByDescending(x => x.MottattAt).Take(antall).ToList();
        try
        {
            return (await _client.From<InnkommendeSms>()
                .Order(x => x.MottattAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(antall).Get()).Models;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av innkommende SMS feilet"); return new(); }
    }

    /// <summary>Innkommende SMS for én sak (nyeste først) — vises på kundekortet.</summary>
    public async Task<List<InnkommendeSms>> ForKundeAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _staging.Where(x => x.KundekortId == kundekortId).OrderByDescending(x => x.MottattAt).ToList();
        try
        {
            return (await _client.From<InnkommendeSms>()
                .Where(x => x.KundekortId == kundekortId)
                .Order(x => x.MottattAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av innkommende SMS for sak feilet"); return new(); }
    }
}
