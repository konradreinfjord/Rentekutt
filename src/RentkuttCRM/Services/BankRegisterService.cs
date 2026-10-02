using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Norske banker hentet fra Finanstilsynets åpne virksomhetsregister (api.finanstilsynet.no).
/// Brukes som fast liste for «Nåværende bank» så navnene blir standardiserte (ikke fritekst).
/// Et foretak regnes som bank når det SELV har BANK-konsesjon (ikke bare opptrer som agent for
/// en bank), og avgrenses til norske foretak (med organisasjonsnummer). Cachet 24 t i minnet;
/// oppdateres av BankRegisterWorker. Feiler aldri hardt — tom liste ⇒ feltet faller tilbake til fritekst.
/// </summary>
public class BankRegisterService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BankRegisterService> _log;
    private static readonly TimeSpan Levetid = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<string> _cache = new();
    private DateTime _hentet;

    public BankRegisterService(IHttpClientFactory http, ILogger<BankRegisterService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Returnerer cachet liste; henter første gang (eller når cache er tom).</summary>
    public async Task<List<string>> HentBankerAsync()
    {
        if (_cache.Count > 0 && DateTime.UtcNow - _hentet < Levetid) return _cache;
        if (_cache.Count == 0) await RefreshAsync();
        return _cache;
    }

    /// <summary>Henter friskt fra APIet og oppdaterer cachen. Kalles av BankRegisterWorker (oppstart + daglig).</summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var friskt = await HentFraApiAsync();
            if (friskt.Count > 0) { _cache = friskt; _hentet = DateTime.UtcNow; }
        }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> HentFraApiAsync()
    {
        var banker = new SortedSet<string>(StringComparer.CurrentCulture);
        try
        {
            var client = _http.CreateClient("finanstilsynet");
            int page = 1, total = int.MaxValue;
            while ((page - 1) * 50 < total && page <= 60)
            {
                using var resp = await client.GetAsync($"registry/v1/legal-entities/filter?licenceTypes=BANK&page={page}");
                if (!resp.IsSuccessStatusCode) break;
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                if (!root.TryGetProperty("legalEntities", out var arr) || arr.ValueKind != JsonValueKind.Array) break;

                foreach (var e in arr.EnumerateArray())
                {
                    var navn = e.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    var orgnr = e.TryGetProperty("organisationNumber", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() : null;
                    var eid = e.TryGetProperty("legalEntityId", out var idEl) && idEl.TryGetInt64(out var idv) ? idv : (long?)null;
                    // Kun norske foretak (har organisasjonsnummer) med et faktisk navn.
                    if (string.IsNullOrWhiteSpace(navn) || string.IsNullOrWhiteSpace(orgnr) || eid is null) continue;
                    if (!e.TryGetProperty("licences", out var lics) || lics.ValueKind != JsonValueKind.Array) continue;

                    // Reell bank = har SELV BANK-konsesjon (licensedEntity == foretaket selv),
                    // ikke bare en linje der foretaket er agent for en annen banks konsesjon.
                    var erBank = false;
                    foreach (var l in lics.EnumerateArray())
                    {
                        var code = l.TryGetProperty("licenceType", out var lt) && lt.TryGetProperty("code", out var c) ? c.GetString() : null;
                        var holder = l.TryGetProperty("licensedEntity", out var le) && le.TryGetProperty("legalEntityId", out var hEl) && hEl.TryGetInt64(out var h) ? h : (long?)null;
                        if (code == "BANK" && holder == eid) { erBank = true; break; }
                    }
                    if (erBank) banker.Add(navn.Trim());
                }
                page++;
            }
            if (banker.Count > 0)
                _log.LogInformation("Bankregister oppdatert fra Finanstilsynet: {Antall} norske banker", banker.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av bankregister fra Finanstilsynet feilet (beholder forrige liste)"); }
        return banker.ToList();
    }
}

/// <summary>Varmer opp og friskner bankregisteret: ved oppstart + hver 24. time.</summary>
public class BankRegisterWorker : BackgroundService
{
    private readonly BankRegisterService _svc;
    private readonly ILogger<BankRegisterWorker> _log;

    public BankRegisterWorker(BankRegisterService svc, ILogger<BankRegisterWorker> log)
    {
        _svc = svc;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await _svc.RefreshAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Oppfriskning av bankregister feilet"); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch { break; }
        }
    }
}
