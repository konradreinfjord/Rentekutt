using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("alarm")]
public class Alarm : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("type")] public string Type { get; set; } = "";
    [Column("alvorlighet")] public string Alvorlighet { get; set; } = "advarsel";
    [Column("tittel")] public string Tittel { get; set; } = "";
    [Column("detalj")] public string? Detalj { get; set; }
    [Column("kilde")] public string? Kilde { get; set; }
    [Column("noekkel")] public string? Noekkel { get; set; }
    [Column("antall")] public int Antall { get; set; } = 1;
    [Column("kvittert")] public bool Kvittert { get; set; }
    [Column("kvittert_av")] public string? KvittertAv { get; set; }
    [Column("kvittert_at")] public DateTime? KvittertAt { get; set; }
    [Column("sist_sett")] public DateTime SistSett { get; set; }
    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; }
}

/// <summary>Alarmer for stopp/feil (migrasjoner, banksending, sikkerhetsbryter).
/// Vises i portalen og må kvitteres ut. Feiler aldri hardt — alarmering skal ikke
/// kunne velte funksjonen som prøver å varsle.</summary>
public class AlarmService
{
    public static class Alvorlighet
    {
        public const string Kritisk = "kritisk";
        public const string Advarsel = "advarsel";
        public const string Info = "info";
    }

    private readonly Supabase.Client _client;
    private readonly ILogger<AlarmService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Alarm> _staging = new();
    private bool _initialized;

    public AlarmService(Supabase.Client client, IConfiguration cfg, ILogger<AlarmService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    /// <summary>Reis en alarm. Finnes det en ÅPEN alarm med samme <paramref name="noekkel"/>,
    /// telles den opp i stedet for å opprette en ny (unngår spam ved gjentatte feil).</summary>
    public async Task RaiseAsync(string type, string tittel, string? detalj = null,
        string alvorlighet = Alvorlighet.Advarsel, string? kilde = null, string? noekkel = null)
    {
        var naa = DateTime.UtcNow;
        try
        {
            if (!IsConfigured)
            {
                var eks = noekkel is null ? null : _staging.FirstOrDefault(a => !a.Kvittert && a.Noekkel == noekkel);
                if (eks is not null) { eks.Antall++; eks.SistSett = naa; eks.Detalj = detalj ?? eks.Detalj; }
                else _staging.Insert(0, new Alarm { Id = Guid.NewGuid(), Type = type, Tittel = tittel, Detalj = detalj, Alvorlighet = alvorlighet, Kilde = kilde, Noekkel = noekkel, SistSett = naa, CreatedAt = naa });
                return;
            }

            await EnsureInitAsync();

            if (noekkel is not null)
            {
                var apne = (await _client.From<Alarm>()
                    .Where(a => a.Kvittert == false && a.Noekkel == noekkel)
                    .Get()).Models;
                var eks = apne.FirstOrDefault();
                if (eks is not null)
                {
                    await _client.From<Alarm>().Where(a => a.Id == eks.Id)
                        .Set(a => a.Antall, eks.Antall + 1)
                        .Set(a => a.SistSett, naa)
                        .Set(a => a.Detalj!, detalj ?? eks.Detalj ?? "")
                        .Update();
                    return;
                }
            }

            await _client.From<Alarm>().Insert(new Alarm
            {
                Type = type, Tittel = tittel, Detalj = detalj, Alvorlighet = alvorlighet,
                Kilde = kilde, Noekkel = noekkel, Antall = 1, Kvittert = false, SistSett = naa,
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke reise alarm ({Type})", type); }
    }

    public async Task<List<Alarm>> ListAsync(bool inkluderKvitterte = false, int limit = 200)
    {
        if (!IsConfigured) return (inkluderKvitterte ? _staging : _staging.Where(a => !a.Kvittert)).Take(limit).ToList();
        try
        {
            await EnsureInitAsync();
            var q = _client.From<Alarm>();
            var filtered = inkluderKvitterte
                ? q.Order(a => a.SistSett, Constants.Ordering.Descending, Constants.NullPosition.Last)
                : q.Where(a => a.Kvittert == false).Order(a => a.SistSett, Constants.Ordering.Descending, Constants.NullPosition.Last);
            return (await filtered.Limit(limit).Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av alarmer feilet"); return new(); }
    }

    public async Task<int> AntallAapneAsync()
    {
        // Info-alarmer (bl.a. GDPR-jobb-hjerteslag) er rene logglinjer og teller ikke som problem.
        if (!IsConfigured) return _staging.Count(a => !a.Kvittert && a.Alvorlighet != Alvorlighet.Info);
        try
        {
            await EnsureInitAsync();
            return (await _client.From<Alarm>()
                .Where(a => a.Kvittert == false && a.Alvorlighet != Alvorlighet.Info).Get()).Models.Count;
        }
        catch (Exception ex) { _log.LogError(ex, "Telling av åpne alarmer feilet"); return 0; }
    }

    public async Task KvitterAsync(Guid id, string avNavn)
    {
        var naa = DateTime.UtcNow;
        if (!IsConfigured)
        {
            var a = _staging.FirstOrDefault(x => x.Id == id);
            if (a is not null) { a.Kvittert = true; a.KvittertAv = avNavn; a.KvittertAt = naa; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<Alarm>().Where(a => a.Id == id)
                .Set(a => a.Kvittert, true)
                .Set(a => a.KvittertAv!, avNavn)
                .Set(a => a.KvittertAt!, naa)
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Kvittering av alarm feilet"); }
    }

    /// <summary>Kvitter ut alle ÅPNE alarmer med gitt nøkkel — brukes til å auto-lukke en alarm
    /// når tilstanden er løst (f.eks. «kryptering AV» når nøkkelen er lastet igjen), slik at en
    /// stale alarm fra et tidligere deploy-vindu ikke blir stående.</summary>
    public async Task LosOppNoekkelAsync(string noekkel, string avNavn)
    {
        var naa = DateTime.UtcNow;
        if (!IsConfigured)
        {
            foreach (var a in _staging.Where(x => !x.Kvittert && x.Noekkel == noekkel))
            { a.Kvittert = true; a.KvittertAv = avNavn; a.KvittertAt = naa; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            var apne = (await _client.From<Alarm>().Where(a => a.Kvittert == false && a.Noekkel == noekkel).Get()).Models;
            foreach (var a in apne)
                await _client.From<Alarm>().Where(x => x.Id == a.Id)
                    .Set(x => x.Kvittert, true).Set(x => x.KvittertAv!, avNavn).Set(x => x.KvittertAt!, naa).Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Auto-lukking av alarm ({Noekkel}) feilet", noekkel); }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
