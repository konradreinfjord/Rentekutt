using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Samtykkebasert innsynstjeneste hos Gjeldsregisteret (via partner) — slå opp en persons usikrede
/// gjeld på fødselsnummer (11 siffer). Autentisering er OAuth2 (client_id/secret + brukernavn/passord),
/// ikke mTLS. Oppslag gjøres KUN på fnr fra kundekort med gyldig 2FA-samtykke (teknisk sperre).
/// Miljø (test/prod), rate-grenser og AV/PÅ styres i Admin.
///
/// Config (Azure app settings — hemmeligheter):
///   Gjeldsregisteret__TokenUrl      = OAuth2 token-endepunkt
///   Gjeldsregisteret__BaseUrl       = API-base for selve oppslaget
///   Gjeldsregisteret__ClientId      = OAuth client-id
///   Gjeldsregisteret__ClientSecret  = OAuth client-secret
///   Gjeldsregisteret__Username      = partner-brukernavn
///   Gjeldsregisteret__Password      = partner-passord
/// </summary>
public class GjeldsregisterService
{
    private const string EnvKey = "gjeldsregister_env";        // "test" | "prod"
    private const string EnabledKey = "gjeldsregister_enabled"; // "true" | "false"
    private const string MaxTimeKey = "gjeldsregister_max_time"; // maks oppslag per time
    private const string MaxDognKey = "gjeldsregister_max_dogn"; // maks oppslag per døgn
    public const int StandardMaxTime = 100;
    public const int StandardMaxDogn = 1000;

    // Enkel glidende-vindu-teller for rate-grensen (per prosess). Holder oppslagstidspunkter.
    private static readonly object _rateLaas = new();
    private static readonly List<DateTime> _oppslagTider = new();

    private readonly IConfiguration _config;
    private readonly SettingsService _settings;
    private readonly SamtykkeService _samtykke;
    private readonly LoggService _logg;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GjeldsregisterService> _log;

    public GjeldsregisterService(IConfiguration config, SettingsService settings, SamtykkeService samtykke,
        LoggService logg, IHttpClientFactory httpFactory, ILogger<GjeldsregisterService> log)
    {
        _config = config;
        _settings = settings;
        _samtykke = samtykke;
        _logg = logg;
        _httpFactory = httpFactory;
        _log = log;
    }

    // ---- Miljø / aktivering (som Instabank) ----
    public async Task<string> MiljoAsync() => (await _settings.GetAsync(EnvKey)) == "prod" ? "prod" : "test";
    public async Task<bool> AktivertAsync() => (await _settings.GetAsync(EnabledKey)) == "true";
    public Task SettMiljoAsync(string env) => _settings.SetAsync(EnvKey, env == "prod" ? "prod" : "test");
    public Task SettAktivertAsync(bool paa) => _settings.SetAsync(EnabledKey, paa ? "true" : "false");

    // ---- Redigerbare rate-grenser ----
    public Task<int> MaxPerTimeAsync() => _settings.GetIntAsync(MaxTimeKey, StandardMaxTime);
    public Task<int> MaxPerDognAsync() => _settings.GetIntAsync(MaxDognKey, StandardMaxDogn);
    public async Task SettGrenserAsync(int perTime, int perDogn)
    {
        await _settings.SetAsync(MaxTimeKey, Math.Max(0, perTime).ToString());
        await _settings.SetAsync(MaxDognKey, Math.Max(0, perDogn).ToString());
    }

    /// <summary>Sjekk rate-grensen (og reserver en plass ved OK). Returnerer feilmelding ved overskridelse,
    /// ellers null. 0 = ingen grense.</summary>
    private async Task<string?> RateLimitAsync()
    {
        var maxTime = await MaxPerTimeAsync();
        var maxDogn = await MaxPerDognAsync();
        var naa = DateTime.UtcNow;
        lock (_rateLaas)
        {
            _oppslagTider.RemoveAll(t => naa - t > TimeSpan.FromDays(1));
            var sisteTime = _oppslagTider.Count(t => naa - t <= TimeSpan.FromHours(1));
            if (maxTime > 0 && sisteTime >= maxTime) return $"Rate-grense nådd: {maxTime} oppslag per time.";
            if (maxDogn > 0 && _oppslagTider.Count >= maxDogn) return $"Rate-grense nådd: {maxDogn} oppslag per døgn.";
            _oppslagTider.Add(naa);
        }
        return null;
    }

    // ---- OAuth2-konfig (app settings) ----
    public string? BaseUrl => _config["Gjeldsregisteret:BaseUrl"];
    private string? TokenUrl => _config["Gjeldsregisteret:TokenUrl"];
    private string? ClientId => _config["Gjeldsregisteret:ClientId"];
    private string? ClientSecret => _config["Gjeldsregisteret:ClientSecret"];
    private string? Username => _config["Gjeldsregisteret:Username"];
    private string? Password => _config["Gjeldsregisteret:Password"];

    /// <summary>Konfigurert når base-URL + legitimasjon er satt. Token-URL er valgfri: er den satt
    /// brukes OAuth2 (Bearer); ellers Basic auth (brukernavn/passord) direkte på kallet.</summary>
    public bool ErKonfigurert =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>Hent OAuth2 bearer-token (password grant: Basic client_id:secret + grant_type=password
    /// med brukernavn/passord). Returnerer (token, feil). Standardmønster — bekreftes mot partner-doc.</summary>
    private async Task<(string? token, string? feil)> HentTokenAsync()
    {
        if (!Uri.TryCreate(TokenUrl?.Trim(), UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps)
            return (null, $"Gjeldsregisteret__TokenUrl er ikke en gyldig absolutt https-URL (fikk: «{TokenUrl}»). Rett verdien, eller la den stå tom for Basic auth.");
        try
        {
            var http = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = Username!,
                ["password"] = Password!,
            });
            using var resp = await http.SendAsync(req);
            var tekst = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (null, $"Token-feil {(int)resp.StatusCode}: {(tekst.Length > 200 ? tekst[..200] : tekst)}");
            using var doc = JsonDocument.Parse(tekst);
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            return string.IsNullOrWhiteSpace(token) ? (null, "Token-svar mangler access_token.") : (token, null);
        }
        catch (Exception ex) { return (null, "Nettverksfeil ved token-henting: " + ex.Message); }
    }

    /// <summary>Test tilkobling/legitimasjon fra Admin: henter ev. token (hvis TokenUrl satt), og gjør
    /// et ping-kall mot BaseUrl. Returnerer en lesbar diagnose med HTTP-status + rå svar (uten hemmeligheter).</summary>
    public async Task<(bool ok, string detalj)> TestTilkoblingAsync()
    {
        if (!ErKonfigurert)
            return (false, "Ikke konfigurert — sett minst Gjeldsregisteret__BaseUrl + __Username + __Password.");

        var sb = new StringBuilder();
        AuthenticationHeaderValue auth;
        if (!string.IsNullOrWhiteSpace(TokenUrl))
        {
            sb.AppendLine($"Auth: OAuth2 (token-URL satt).");
            var (token, feil) = await HentTokenAsync();
            if (token is null) { sb.AppendLine("Token-henting: FEILET — " + feil); return (false, sb.ToString()); }
            sb.AppendLine("Token-henting: OK ✓");
            auth = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            sb.AppendLine("Auth: Basic (brukernavn/passord).");
            auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));
        }

        var pingUrl = BaseUrl!.TrimEnd('/') + "/ping/hello";
        if (!Uri.TryCreate(pingUrl, UriKind.Absolute, out var pingUri) || pingUri.Scheme != Uri.UriSchemeHttps)
        {
            sb.AppendLine($"Gjeldsregisteret__BaseUrl er ikke en gyldig absolutt https-URL (fikk: «{BaseUrl}»).");
            return (false, sb.ToString());
        }
        try
        {
            var http = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, pingUri);
            req.Headers.Authorization = auth;
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            sb.AppendLine($"GET {pingUrl}");
            sb.AppendLine($"→ HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if ((int)resp.StatusCode == 403)
                sb.AppendLine("Hint: 403 uten WWW-Authenticate = klientsertifikat (mTLS) kreves — denne URL-en tar ikke brukernavn/passord.");
            if (!string.IsNullOrWhiteSpace(body))
                sb.AppendLine("Svar: " + (body.Length > 500 ? body[..500] + "…" : body));
            return (resp.IsSuccessStatusCode, sb.ToString());
        }
        catch (Exception ex) { sb.AppendLine("Nettverksfeil: " + ex.Message); return (false, sb.ToString()); }
    }

    public record GjeldResultat(bool Ok, bool Avvist, string Melding, decimal? UsikretGjeld = null, string? RaaSvar = null);

    /// <summary>
    /// Slå opp usikret gjeld for kunden. TEKNISK SPERRE: krever gyldig 2FA-samtykke
    /// (gjeldsregister/kredittsjekk) — ellers avvises oppslaget og logges. Selve mTLS-kallet mot
    /// Infotorg wires når virksomhetssertifikatet og Infotorg-søkeskjemaet er på plass.
    /// </summary>
    public async Task<GjeldResultat> SlaaOppGjeldAsync(Kundekort k, string? aktor = null)
    {
        // 1) Samtykke-sperre — ingen 2FA-samtykke = ingen spørring.
        var harSamtykke = await _samtykke.HarGyldigEllerLegacyAsync(k.Id, SamtykkeService.FormaalKreditt, k.SamtykkeGjeldsregisterKredittsjekk);
        if (!harSamtykke)
        {
            await _logg.LoggAsync(k.Id, aktor, "Gjeldsregister-oppslag AVVIST — mangler gyldig 2FA-samtykke (gjeldsregister/kredittsjekk).", kategori: "avgjørelse");
            return new(false, true, "Mangler gyldig 2FA-samtykke (Gjeldsregister og kredittsjekk) — oppslag ikke tillatt.");
        }

        // 2) Gyldig fnr på kortet.
        var fnr = new string((k.Foedselsnummer ?? "").Where(char.IsDigit).ToArray());
        if (fnr.Length != 11 || !Fnr.ErGyldig(fnr))
            return new(false, true, "Mangler gyldig fødselsnummer på kundekortet.");

        // 3) Aktivering + konfigurasjon.
        if (!await AktivertAsync())
            return new(false, false, "Gjeldsregister-oppslag er slått AV i Admin.");
        var env = await MiljoAsync();
        if (!ErKonfigurert)
            return new(false, false, "Gjeldsregister er ikke konfigurert — sett Gjeldsregisteret__TokenUrl/BaseUrl/ClientId/ClientSecret/Username/Password i Azure.");

        // 4) Rate-grense (redigerbar i Admin).
        var rateFeil = await RateLimitAsync();
        if (rateFeil is not null)
        {
            await _logg.LoggAsync(k.Id, aktor, $"Gjeldsregister-oppslag stoppet av rate-grense: {rateFeil}", kategori: "avgjørelse");
            return new(false, false, rateFeil);
        }

        // 5) Auth: OAuth2 Bearer hvis TokenUrl er satt, ellers Basic (brukernavn/passord) direkte.
        AuthenticationHeaderValue authHeader;
        if (!string.IsNullOrWhiteSpace(TokenUrl))
        {
            var (token, tokenFeil) = await HentTokenAsync();
            if (token is null)
            {
                await _logg.LoggAsync(k.Id, aktor, $"Gjeldsregister-oppslag ({env}) feilet ved token: {tokenFeil}", kategori: "avgjørelse");
                return new(false, false, tokenFeil ?? "Kunne ikke hente token.");
            }
            authHeader = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            authHeader = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));
        }

        // 6) Oppslag på fnr. Request/response-format bekreftes mot faktisk test-svar — vi POSTer fnr og
        //    returnerer rå respons, så feltmapping (usikret gjeld) settes når vi ser det ekte formatet.
        try
        {
            var http = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
            req.Headers.Authorization = authHeader;
            req.Content = new StringContent(JsonSerializer.Serialize(new { ssn = fnr }), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var svar = await resp.Content.ReadAsStringAsync();
            await _logg.LoggAsync(k.Id, aktor, $"Gjeldsregister-oppslag ({env}) utført — HTTP {(int)resp.StatusCode}.", kategori: "kobling");
            if (!resp.IsSuccessStatusCode)
                return new(false, false, $"Oppslag feilet {(int)resp.StatusCode}: {(svar.Length > 200 ? svar[..200] : svar)}", RaaSvar: svar);
            return new(true, false, "Oppslag OK — rå respons mottatt (feltmapping bekreftes mot format).", RaaSvar: svar);
        }
        catch (Exception ex) { return new(false, false, "Nettverksfeil ved oppslag: " + ex.Message); }
    }
}
