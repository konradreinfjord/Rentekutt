using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>Én logglinje for en automatisk SMS-utsending (dedup + historikk).</summary>
[Table("sms_utsending")]
public class SmsUtsending : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid KundekortId { get; set; }
    [Column("type")] public string Type { get; set; } = "";
    [Column("mobil")] public string? Mobil { get; set; }
    [Column("ok")] public bool Ok { get; set; }
    [Column("detalj")] public string? Detalj { get; set; }
    [Column("sendt_at", ignoreOnInsert: true)] public DateTime SendtAt { get; set; }
}

/// <summary>Sporing av automatiske SMS-utsendinger. Hindrer duplikater og gir logg til Kommunikasjon-fanen.</summary>
public class SmsUtsendingService
{
    public const string TypePaamindelse24t = "paamindelse_24t";

    private readonly Supabase.Client _client;
    private readonly ILogger<SmsUtsendingService> _log;
    public bool IsConfigured { get; }
    private static readonly List<SmsUtsending> _staging = new();

    public SmsUtsendingService(Supabase.Client client, IConfiguration cfg, ILogger<SmsUtsendingService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Sant hvis det finnes en vellykket utsending av gitt type for saken (→ ikke send igjen).</summary>
    public async Task<bool> HarSendtOkAsync(Guid kundekortId, string type)
    {
        if (!IsConfigured) return _staging.Any(x => x.KundekortId == kundekortId && x.Type == type && x.Ok);
        try
        {
            var treff = (await _client.From<SmsUtsending>()
                .Where(x => x.KundekortId == kundekortId && x.Type == type && x.Ok == true)
                .Limit(1).Get()).Models;
            return treff.Count > 0;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Sjekk av tidligere SMS-utsending feilet"); return false; }
    }

    public async Task LoggAsync(Guid kundekortId, string type, string? mobil, bool ok, string? detalj)
    {
        var rad = new SmsUtsending { KundekortId = kundekortId, Type = type, Mobil = mobil, Ok = ok, Detalj = detalj };
        if (!IsConfigured) { rad.Id = Guid.NewGuid(); rad.SendtAt = DateTime.UtcNow; _staging.Insert(0, rad); return; }
        try { await _client.From<SmsUtsending>().Insert(rad); }
        catch (Exception ex) { _log.LogError(ex, "Logging av SMS-utsending feilet"); }
    }

    public async Task<List<SmsUtsending>> SisteAsync(int antall = 50)
    {
        if (!IsConfigured) return _staging.OrderByDescending(x => x.SendtAt).Take(antall).ToList();
        try
        {
            return (await _client.From<SmsUtsending>()
                .Order(x => x.SendtAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(antall).Get()).Models;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av SMS-utsendingslogg feilet"); return new(); }
    }
}
