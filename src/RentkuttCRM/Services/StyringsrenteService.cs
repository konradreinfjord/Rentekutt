using System.Globalization;

namespace RentkuttCRM.Services;

/// <summary>
/// Henter Norges Banks styringsrente (key policy rate) live fra det åpne
/// data-API-et og cacher endringspunktene. Faller tilbake til en innebygd
/// referanseliste hvis API-et er utilgjengelig, slik at Markedsinnsikt alltid
/// kan tegnes.
///
/// API: https://data.norges-bank.no/api/data/IR/B.KPRA.SD.R?format=csv
/// (IR = renter, KPRA = key policy rate, daglig frekvens). Vi komprimerer den
/// daglige serien til kun datoene der renten faktisk endrer seg.
/// </summary>
public class StyringsrenteService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<StyringsrenteService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<(DateTime Dato, decimal Rente)>? _cache;
    private DateTime _hentet;
    public string Kilde { get; private set; } = "referanse";

    public StyringsrenteService(IHttpClientFactory http, ILogger<StyringsrenteService> log)
    {
        _http = http;
        _log = log;
    }

    // Innebygd fallback — Norges Banks endringsdatoer. Brukes hvis API-et feiler.
    private static readonly (DateTime Dato, decimal Rente)[] Fallback =
    {
        (new DateTime(2021, 9, 24), 0.25m),
        (new DateTime(2021, 12, 17), 0.50m),
        (new DateTime(2022, 3, 24), 0.75m),
        (new DateTime(2022, 6, 23), 1.25m),
        (new DateTime(2022, 8, 18), 1.75m),
        (new DateTime(2022, 9, 22), 2.25m),
        (new DateTime(2022, 11, 3), 2.50m),
        (new DateTime(2022, 12, 15), 2.75m),
        (new DateTime(2023, 3, 23), 3.00m),
        (new DateTime(2023, 5, 4), 3.25m),
        (new DateTime(2023, 6, 22), 3.75m),
        (new DateTime(2023, 8, 17), 4.00m),
        (new DateTime(2023, 9, 21), 4.25m),
        (new DateTime(2023, 12, 14), 4.50m),
        (new DateTime(2025, 6, 19), 4.25m),
    };

    public async Task<IReadOnlyList<(DateTime Dato, decimal Rente)>> HentAsync()
    {
        // Cache i 12 timer (styringsrenten endres sjelden).
        if (_cache is not null && DateTime.UtcNow - _hentet < TimeSpan.FromHours(12))
            return _cache;

        await _gate.WaitAsync();
        try
        {
            if (_cache is not null && DateTime.UtcNow - _hentet < TimeSpan.FromHours(12))
                return _cache;

            var live = await HentFraApiAsync();
            if (live.Count >= 2)
            {
                _cache = live;
                _hentet = DateTime.UtcNow;
                Kilde = "Norges Bank (live)";
                return _cache;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Henting av styringsrente fra Norges Bank feilet — bruker referanseliste");
        }
        finally
        {
            _gate.Release();
        }

        Kilde = "referanse";
        return Fallback;
    }

    private async Task<List<(DateTime Dato, decimal Rente)>> HentFraApiAsync()
    {
        const string url = "https://data.norges-bank.no/api/data/IR/B.KPRA.SD.R?format=csv&startPeriod=2015-01-01&locale=en";
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(6);
        var csv = await client.GetStringAsync(url);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new();

        // Norges Bank CSV er semikolon-separert; finn TIME_PERIOD og OBS_VALUE.
        char sep = lines[0].Contains(';') ? ';' : ',';
        var header = SplitCsv(lines[0], sep);
        int iDato = Array.FindIndex(header, h => h.Trim('"').Equals("TIME_PERIOD", StringComparison.OrdinalIgnoreCase));
        int iVerdi = Array.FindIndex(header, h => h.Trim('"').Equals("OBS_VALUE", StringComparison.OrdinalIgnoreCase));
        if (iDato < 0 || iVerdi < 0) return new();

        var raa = new List<(DateTime Dato, decimal Rente)>();
        for (var i = 1; i < lines.Length; i++)
        {
            var col = SplitCsv(lines[i], sep);
            if (col.Length <= Math.Max(iDato, iVerdi)) continue;
            if (!DateTime.TryParse(col[iDato].Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!decimal.TryParse(col[iVerdi].Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var r)) continue;
            raa.Add((d, r));
        }
        raa.Sort((a, b) => a.Dato.CompareTo(b.Dato));

        // Komprimér daglig serie til endringspunkter.
        var endringer = new List<(DateTime, decimal)>();
        decimal? forrige = null;
        foreach (var p in raa)
        {
            if (forrige is null || p.Rente != forrige)
            {
                endringer.Add(p);
                forrige = p.Rente;
            }
        }
        return endringer;
    }

    private static string[] SplitCsv(string line, char sep) => line.TrimEnd('\r').Split(sep);
}
