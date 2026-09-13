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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KlaviyoService> _log;

    // Cache av «klaviyo_enabled» (unngår en DB-lesning per event). Oppdateres hvert minutt.
    private bool _aktivertCache;
    private DateTime _aktivertUtløper = DateTime.MinValue;

    public KlaviyoService(IHttpClientFactory http, IConfiguration cfg, IServiceScopeFactory scopeFactory, ILogger<KlaviyoService> log)
    {
        _http = http;
        _cfg = cfg;
        _scopeFactory = scopeFactory;
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

    /// <summary>Master-bryteren (klaviyo_enabled) fra innstillinger, cachet i 60 s.</summary>
    public async Task<bool> AktivertAsync()
    {
        if (DateTime.UtcNow < _aktivertUtløper) return _aktivertCache;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            _aktivertCache = await settings.GetBoolAsync(KeyEnabled, false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke lese klaviyo_enabled"); }
        _aktivertUtløper = DateTime.UtcNow.AddSeconds(60);
        return _aktivertCache;
    }

    /// <summary>Oppretter/oppdaterer en profil (kunde) i Klaviyo via profile-import (upsert på
    /// e-post/telefon/external_id). 201 = ny, 200 = oppdatert.</summary>
    public async Task<(bool Ok, int Status, string Detalj)> UpsertProfilAsync(
        string? epost, string? telefon, string? navn, string? externalId,
        IDictionary<string, object?>? egenskaper = null, CancellationToken ct = default)
    {
        if (!ErKonfigurert) return (false, 0, "Klaviyo er ikke konfigurert.");
        var tlf = NormaliserTelefon(telefon);
        if (string.IsNullOrWhiteSpace(epost) && string.IsNullOrWhiteSpace(tlf) && string.IsNullOrWhiteSpace(externalId))
            return (false, 0, "Mangler e-post/telefon/external_id — kan ikke identifisere profil.");

        var (fornavn, etternavn) = DelNavn(navn);
        var attrs = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(epost)) attrs["email"] = epost.Trim();
        if (!string.IsNullOrWhiteSpace(tlf)) attrs["phone_number"] = tlf;
        if (!string.IsNullOrWhiteSpace(externalId)) attrs["external_id"] = externalId;
        if (!string.IsNullOrWhiteSpace(fornavn)) attrs["first_name"] = fornavn;
        if (!string.IsNullOrWhiteSpace(etternavn)) attrs["last_name"] = etternavn;
        if (egenskaper is { Count: > 0 })
            attrs["properties"] = egenskaper.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

        var payload = new { data = new { type = "profile", attributes = attrs } };
        try
        {
            using var c = Klient();
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await c.PostAsync("api/profile-import/", content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return (true, (int)res.StatusCode, "Profil opprettet/oppdatert i Klaviyo.");
            _log.LogWarning("Klaviyo profile-import feilet {Status}: {Body}", (int)res.StatusCode, body);
            return (false, (int)res.StatusCode, Kort(body));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Klaviyo profile-import kastet unntak"); return (false, 0, ex.Message); }
    }

    /// <summary>Opprett/oppdater kunde + send event — fire-and-forget (blokkerer aldri hovedflyten,
    /// og gjør ingenting hvis Klaviyo er av eller ukonfigurert). Trygg å kalle fra hvor som helst.</summary>
    public void Fyr(Kundekort k, string metric, IDictionary<string, object?>? ekstraEventEgenskaper = null)
    {
        if (k is null || !ErKonfigurert) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await AktivertAsync()) return;
                var epost = k.Epost;

                // Dataminimering: kun navn + kontaktinfo + lånetype sendes til Klaviyo. external_id er
                // kundekort-id-en (teknisk matchingsnøkkel for upsert, ikke persondata).
                var profilEgenskaper = new Dictionary<string, object?> { ["laanetype"] = k.Laanetype };
                await UpsertProfilAsync(epost, k.Mobilnummer, k.FulltNavn, k.Id.ToString(), profilEgenskaper);

                if (!string.IsNullOrWhiteSpace(epost))
                {
                    var eventEgen = new Dictionary<string, object?> { ["laanetype"] = k.Laanetype };
                    if (ekstraEventEgenskaper is not null)
                        foreach (var kv in ekstraEventEgenskaper) eventEgen[kv.Key] = kv.Value;
                    await SendEventAsync(metric, epost, eventEgen);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Klaviyo Fyr({Metric}) feilet", metric); }
        });
    }

    // Enkel norsk E.164-normalisering; returnerer null hvis nummeret ikke gir mening (så Klaviyo ikke avviser).
    private static string? NormaliserTelefon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var plus = raw.TrimStart().StartsWith("+");
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (plus && d.Length >= 8) return "+" + d;
        if (d.Length == 8) return "+47" + d;
        if (d.StartsWith("47") && d.Length == 10) return "+" + d;
        if (d.StartsWith("00") && d.Length >= 10) return "+" + d[2..];
        return null;   // ukjent format → utelat (unngår 400 fra Klaviyo)
    }

    private static (string? fornavn, string? etternavn) DelNavn(string? navn)
    {
        if (string.IsNullOrWhiteSpace(navn)) return (null, null);
        var deler = navn.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return deler.Length == 1 ? (deler[0], null) : (deler[0], deler[1]);
    }

    private static string Kort(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s[..300] + "…" : s);
}
