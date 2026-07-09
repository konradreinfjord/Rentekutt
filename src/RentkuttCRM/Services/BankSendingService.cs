using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("banksending")]
public class BankSending : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kundekort_id")] public Guid? KundekortId { get; set; }
    [Column("kunde_navn")] public string? KundeNavn { get; set; }
    [Column("bank")] public string Bank { get; set; } = "";
    [Column("status")] public string? Status { get; set; }
    [Column("ekstern_ref")] public string? EksternRef { get; set; }
    [Column("signing_url")] public string? SigningUrl { get; set; }
    [Column("detalj")] public string? Detalj { get; set; }
    [Column("sendt_av")] public string? SendtAv { get; set; }
    [Column("sendt_at", ignoreOnInsert: true)] public DateTime SendtAt { get; set; }
}

/// <summary>Logg over søknader sendt til bank. Vises på kundekortet og under Bank API.</summary>
public class BankSendingService
{
    private readonly Supabase.Client _client;
    private readonly ILogger<BankSendingService> _log;
    public bool IsConfigured { get; }

    private static readonly List<BankSending> _staging = new();
    private bool _initialized;

    public BankSendingService(Supabase.Client client, IConfiguration cfg, ILogger<BankSendingService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<BankSending> LoggAsync(BankSending s)
    {
        if (!IsConfigured)
        {
            s.Id = Guid.NewGuid();
            s.SendtAt = DateTime.UtcNow;
            _staging.Insert(0, s);
            return s;
        }
        try
        {
            await EnsureInitAsync();
            var resp = await _client.From<BankSending>().Insert(s);
            return resp.Models.FirstOrDefault() ?? s;
        }
        catch (Exception ex) { _log.LogError(ex, "Logging av banksending feilet"); return s; }
    }

    public async Task<List<BankSending>> ForKundeAsync(Guid kundekortId)
    {
        if (!IsConfigured) return _staging.Where(x => x.KundekortId == kundekortId).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<BankSending>()
                .Where(x => x.KundekortId == kundekortId)
                .Order(x => x.SendtAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av banksendinger for kunde feilet"); return new(); }
    }

    public async Task<List<BankSending>> SisteAsync(int limit = 25)
    {
        if (!IsConfigured) return _staging.Take(limit).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<BankSending>()
                .Order(x => x.SendtAt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(limit).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av siste banksendinger feilet"); return new(); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
