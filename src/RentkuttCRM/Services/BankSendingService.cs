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
    [Column("produkt")] public string? Produkt { get; set; }
    [Column("produkt_kode")] public int? ProduktKode { get; set; }
    [Column("status")] public string? Status { get; set; }
    [Column("ekstern_ref")] public string? EksternRef { get; set; }
    [Column("signing_url")] public string? SigningUrl { get; set; }
    [Column("detalj")] public string? Detalj { get; set; }
    [Column("sendt_av")] public string? SendtAv { get; set; }
    [Column("forsok")] public int Forsok { get; set; }
    [Column("sendt_at", ignoreOnInsert: true)] public DateTime SendtAt { get; set; }
}

/// <summary>Kø-statuser for banksending.</summary>
public static class SendStatus
{
    public const string IKo = "I kø";
    public const string Sendt = "Sendt";
    public const string Feilet = "Feilet";
    public const string Manuelt = "Registrert manuelt";
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

    /// <summary>Hent de eldste sendingene i kø (throttlet av bakgrunnsarbeideren).</summary>
    public async Task<List<BankSending>> HentKoAsync(int limit = 5)
    {
        if (!IsConfigured) return _staging.Where(x => x.Status == SendStatus.IKo)
            .OrderBy(x => x.SendtAt).Take(limit).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<BankSending>()
                .Where(x => x.Status == SendStatus.IKo)
                .Order(x => x.SendtAt, Constants.Ordering.Ascending, Constants.NullPosition.Last)
                .Limit(limit).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av sendekø feilet"); return new(); }
    }

    /// <summary>Oppdater en sending etter behandling (status, referanser, forsøk).</summary>
    public async Task OppdaterAsync(BankSending s)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == s.Id);
            if (k is not null) { k.Status = s.Status; k.EksternRef = s.EksternRef; k.SigningUrl = s.SigningUrl; k.Detalj = s.Detalj; k.Forsok = s.Forsok; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<BankSending>().Where(x => x.Id == s.Id)
                .Set(x => x.Status!, s.Status)
                .Set(x => x.EksternRef!, s.EksternRef ?? "")
                .Set(x => x.SigningUrl!, s.SigningUrl ?? "")
                .Set(x => x.Detalj!, s.Detalj ?? "")
                .Set(x => x.Forsok, s.Forsok)
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Oppdatering av banksending feilet"); }
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
