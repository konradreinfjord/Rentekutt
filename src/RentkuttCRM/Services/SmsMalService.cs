using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("sms_maler")]
public class SmsMal : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("navn")] public string Navn { get; set; } = "";
    [Column("tekst")] public string Tekst { get; set; } = "";
    [Column("created_at", ignoreOnInsert: true)] public DateTime CreatedAt { get; set; }
}

/// <summary>
/// SMS-maler + utsending til kunder (via LinkMobility). Automatikk styres av innstillinger.
/// </summary>
public class SmsMalService
{
    public const string KeyAutoEnabled = "sms_auto_ny_soknad_enabled";
    public const string KeyAutoMal = "sms_auto_ny_soknad_mal";

    private readonly Supabase.Client _client;
    private readonly LinkMobilityService _sms;
    private readonly SettingsService _settings;
    private readonly ILogger<SmsMalService> _log;
    public bool IsConfigured { get; }

    private static readonly List<SmsMal> _staging = new();
    private bool _initialized;

    public SmsMalService(Supabase.Client client, LinkMobilityService sms, SettingsService settings,
        IConfiguration cfg, ILogger<SmsMalService> log)
    {
        _client = client;
        _sms = sms;
        _settings = settings;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<List<SmsMal>> ListAsync()
    {
        if (!IsConfigured) return _staging.OrderBy(m => m.Navn).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<SmsMal>().Order(x => x.Navn, Constants.Ordering.Ascending, Constants.NullPosition.Last).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av SMS-maler feilet"); return new(); }
    }

    public async Task<(bool ok, string? error)> AddAsync(string navn, string tekst)
    {
        navn = (navn ?? "").Trim();
        tekst = (tekst ?? "").Trim();
        if (string.IsNullOrWhiteSpace(navn) || string.IsNullOrWhiteSpace(tekst)) return (false, "Navn og tekst er påkrevd.");
        var m = new SmsMal { Navn = navn, Tekst = tekst };
        if (!IsConfigured) { m.Id = Guid.NewGuid(); _staging.Add(m); return (true, null); }
        try
        {
            await EnsureInitAsync();
            await _client.From<SmsMal>().Insert(m);
            return (true, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Oppretting av SMS-mal feilet"); return (false, "Teknisk feil ved lagring."); }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(m => m.Id == id); return; }
        try { await EnsureInitAsync(); await _client.From<SmsMal>().Where(x => x.Id == id).Delete(); }
        catch (Exception ex) { _log.LogError(ex, "Sletting av SMS-mal feilet"); }
    }

    /// <summary>Send SMS til en kunde. Erstatter {navn} med kundens navn.</summary>
    public async Task<(bool ok, string detalj)> SendTilKundeAsync(string? mobil, string tekst, string? kundeNavn)
    {
        if (string.IsNullOrWhiteSpace(mobil)) return (false, "Kunden mangler mobilnummer.");
        if (string.IsNullOrWhiteSpace(tekst)) return (false, "Meldingen er tom.");
        var melding = Personaliser(tekst, kundeNavn);
        var (ok, _, detalj) = await _sms.SendSmsAsync(mobil, melding);
        return (ok, detalj);
    }

    /// <summary>Kalles når en ny søknad registreres — sender automatisk SMS hvis slått på.</summary>
    public async Task MaybeSendAutomatikkAsync(Kundekort k)
    {
        try
        {
            var enabled = (await _settings.GetAsync(KeyAutoEnabled)) == "true";
            if (!enabled) return;
            var malNavn = await _settings.GetAsync(KeyAutoMal);
            if (string.IsNullOrWhiteSpace(malNavn)) return;
            var mal = (await ListAsync()).FirstOrDefault(m => m.Navn == malNavn);
            if (mal is null || string.IsNullOrWhiteSpace(k.Mobilnummer)) return;
            await SendTilKundeAsync(k.Mobilnummer, mal.Tekst, k.FulltNavn);
            _log.LogInformation("Automatisk SMS sendt til ny søknad (mal {Mal})", malNavn);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Automatisk SMS feilet"); }
    }

    private static string Personaliser(string tekst, string? navn)
    {
        var fornavn = string.IsNullOrWhiteSpace(navn) ? "" : navn.Split(' ')[0];
        return tekst.Replace("{navn}", fornavn, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
