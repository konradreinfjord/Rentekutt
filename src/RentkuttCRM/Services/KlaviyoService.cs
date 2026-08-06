using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Klaviyo-integrasjon (Events API). Klargjort for å sende hendelser fra portalen til Klaviyo.
/// API-nøkkelen (privat) leses KUN fra server-config (Azure App Settings: Klaviyo__ApiKey),
/// aldri fra databasen. Tjenesten degraderer pent når den ikke er konfigurert.
/// </summary>
public class KlaviyoService
{
    // Klaviyo krever en «revision»-header (API-versjon). Kan overstyres via config.
    private const string StandardRevisjon = "2025-01-15";

    public const string KeyEnabled = "klaviyo_enabled";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<KlaviyoService> _log;

    public KlaviyoService(IHttpClientFactory http, IConfiguration cfg, ILogger<KlaviyoService> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    private string? ApiKey => _cfg["Klaviyo:ApiKey"];
    public string Revisjon => _cfg["Klaviyo:Revision"] ?? StandardRevisjon;
    public bool ErKonfigurert => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Hendelsestypene (metrics) portalen er klargjort for å sende til Klaviyo.</summary>
    public static readonly (string Metric, string Beskrivelse)[] Eventtyper =
    {
        ("Nytt lead", "Når et nytt lead/søknad kommer inn (kanal, prismatch eller manuelt)."),
        ("Påbegynt søknad", "Når en søknad settes i status «Påbegynt søknad»."),
        ("Status endret", "Når en søknads status endres."),
        ("Søknad sendt til bank", "Når en søknad sendes til bankpartner."),
    };

    private HttpClient Klient()
    {
        var c = _http.CreateClient("klaviyo");
        c.BaseAddress = new Uri("https://a.klaviyo.com/");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Klaviyo-API-Key {ApiKey}");
        c.DefaultRequestHeaders.TryAddWithoutValidation("revision", Revisjon);
        c.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        return c;
    }

    /// <summary>Lettvekts test av API-nøkkelen (henter metrics). Brukes av Test-knappen i admin.</summary>
    public async Task<(bool Ok, int Status, string Detalj)> TestTilkoblingAsync(CancellationToken ct = default)
    {
        if (!ErKonfigurert) return (false, 0, "Klaviyo er ikke konfigurert (mangler Klaviyo__ApiKey i Azure).");
        try
        {
            using var c = Klient();
            using var res = await c.GetAsync("api/metrics/?page%5Bsize%5D=1", ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return res.IsSuccessStatusCode
                ? (true, (int)res.StatusCode, "Tilkobling OK — API-nøkkel er gyldig.")
                : (false, (int)res.StatusCode, Kort(body));
        }
        catch (Exception ex) { return (false, 0, ex.Message); }
    }

    /// <summary>Sender en hendelse (event) til Klaviyo, knyttet til en profil via e-post.</summary>
    public async Task<(bool Ok, int Status, string Detalj)> SendEventAsync(
        string metric, string epost, IDictionary<string, object?>? egenskaper = null, CancellationToken ct = default)
    {
        if (!ErKonfigurert) return (false, 0, "Klaviyo er ikke konfigurert.");
        if (string.IsNullOrWhiteSpace(epost)) return (false, 0, "Mangler e-post på profilen.");

        var payload = new
        {
            data = new
            {
                type = "event",
                attributes = new
                {
                    properties = egenskaper ?? new Dictionary<string, object?>(),
                    metric = new { data = new { type = "metric", attributes = new { name = metric } } },
                    profile = new { data = new { type = "profile", attributes = new { email = epost } } },
                },
            },
        };

        try
        {
            using var c = Klient();
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await c.PostAsync("api/events/", content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return (true, (int)res.StatusCode, "Event sendt til Klaviyo.");
            _log.LogWarning("Klaviyo-event feilet {Status}: {Body}", (int)res.StatusCode, body);
            return (false, (int)res.StatusCode, Kort(body));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Klaviyo-event kastet unntak"); return (false, 0, ex.Message); }
    }

    private static string Kort(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s[..300] + "…" : s);
}
