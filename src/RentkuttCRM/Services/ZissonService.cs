using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Integrasjon mot Zisson «Wave» External API (app2.zisson.com) — dialer/click-to-call.
///
/// Autentisering: kortlevd JWT hentes via POST /web-api/v1/authenticate/refresh-token med
/// {customerGuid, id, refreshToken}. refreshToken roterer og persisteres i innstillinger
/// (nøkkel <see cref="RefreshTokenKey"/>), seedet fra config første gang. Hemmeligheter
/// (passord, refreshToken) ligger i Azure-config; customerGuid/id/brukernavn i appsettings.
/// </summary>
public class ZissonService
{
    private const string RefreshTokenKey = "zisson_refresh_token";
    private const string CallerIdKey = "zisson_default_callerid";
    private const string EnabledKey = "zisson_enabled";
    private const string AutoAnswerKey = "zisson_auto_answer";

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<ZissonService> _log;

    private string? _jwt;
    private DateTime _jwtUtløper = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLås = new(1, 1);

    public ZissonService(IConfiguration config, IHttpClientFactory httpFactory, SettingsService settings, ILogger<ZissonService> log)
    {
        _config = config;
        _httpFactory = httpFactory;
        _settings = settings;
        _log = log;
    }

    private string BaseUrl => (_config["Zisson:BaseUrl"] ?? "https://app2.zisson.com").TrimEnd('/');
    private string? CustomerGuid => _config["Zisson:CustomerGuid"];
    private string? LoginId => _config["Zisson:Id"];
    private string? RefreshTokenConfig => _config["Zisson:RefreshToken"];

    public bool HarGrunnkonfig =>
        !string.IsNullOrWhiteSpace(CustomerGuid) && !string.IsNullOrWhiteSpace(LoginId);

    public async Task<bool> ErKonfigurertAsync()
        => HarGrunnkonfig && !string.IsNullOrWhiteSpace(await GjeldendeRefreshTokenAsync());

    public async Task<bool> AktivertAsync() => (await _settings.GetAsync(EnabledKey)) != "false"; // på som standard
    public Task SettAktivertAsync(bool på) => _settings.SetAsync(EnabledKey, på ? "true" : "false");

    public async Task<bool> AutoAnswerAsync() => (await _settings.GetAsync(AutoAnswerKey)) != "false"; // på som standard
    public Task SettAutoAnswerAsync(bool på) => _settings.SetAsync(AutoAnswerKey, på ? "true" : "false");

    public Task<string?> DefaultCallerIdAsync() => _settings.GetAsync(CallerIdKey);
    public Task SettDefaultCallerIdAsync(string? nummer) => _settings.SetAsync(CallerIdKey, string.IsNullOrWhiteSpace(nummer) ? null : nummer.Trim());

    private async Task<string?> GjeldendeRefreshTokenAsync()
        => (await _settings.GetAsync(RefreshTokenKey)) ?? RefreshTokenConfig;

    // ---- Nummer-normalisering (E.164 / +47) ----------------------------------------------

    /// <summary>Normaliserer et norsk mobilnummer til E.164 (+47…). Beholder allerede
    /// internasjonale numre (+/00). Returnerer null hvis nummeret ikke gir mening.</summary>
    public static string? NormaliserNummer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var plus = s.StartsWith("+") || s.StartsWith("00");
        var siffer = new string(s.Where(char.IsDigit).ToArray());
        if (s.StartsWith("00")) siffer = siffer.Length > 2 ? siffer[2..] : siffer; // 00 → landkode
        if (siffer.Length == 0) return null;
        if (plus) return "+" + siffer;                 // allerede internasjonalt
        if (siffer.Length == 8) return "+47" + siffer; // norsk mobil/fasttelefon
        if (siffer.StartsWith("47") && siffer.Length == 10) return "+" + siffer;
        return "+" + siffer;                            // ukjent format – best effort
    }

    // ---- Token ---------------------------------------------------------------------------

    private async Task<string?> HentJwtAsync()
    {
        if (_jwt is not null && DateTime.UtcNow < _jwtUtløper) return _jwt;
        await _tokenLås.WaitAsync();
        try
        {
            if (_jwt is not null && DateTime.UtcNow < _jwtUtløper) return _jwt;

            var refreshToken = await GjeldendeRefreshTokenAsync();
            if (!HarGrunnkonfig || string.IsNullOrWhiteSpace(refreshToken)) return null;

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            var body = JsonSerializer.Serialize(new
            {
                customerGuid = CustomerGuid,
                id = LoginId,
                refreshToken,
                sessionGuid = Guid.NewGuid().ToString(),
            });
            using var resp = await http.PostAsync($"{BaseUrl}/web-api/v1/authenticate/refresh-token",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Zisson token-fornying feilet: {Status}", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            var jwt = root.TryGetProperty("jwt", out var j) ? j.GetString() : null;
            var nyttRefresh = root.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;
            if (string.IsNullOrWhiteSpace(jwt)) return null;

            // Persister rotert refreshToken slik at neste fornying virker.
            if (!string.IsNullOrWhiteSpace(nyttRefresh) && nyttRefresh != refreshToken)
                await _settings.SetAsync(RefreshTokenKey, nyttRefresh);

            _jwt = jwt;
            _jwtUtløper = JwtUtløp(jwt) ?? DateTime.UtcNow.AddMinutes(10);
            return _jwt;
        }
        catch (Exception ex) { _log.LogError(ex, "Zisson token-henting feilet"); return null; }
        finally { _tokenLås.Release(); }
    }

    // Leser exp-claim fra JWT og trekker fra 60 s margin. Null hvis ikke lesbart.
    private static DateTime? JwtUtløp(string jwt)
    {
        try
        {
            var deler = jwt.Split('.');
            if (deler.Length < 2) return null;
            var p = deler[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var sek))
                return DateTimeOffset.FromUnixTimeSeconds(sek).UtcDateTime.AddSeconds(-60);
        }
        catch { /* ignorér – bruker fallback */ }
        return null;
    }

    private async Task<HttpClient?> KlientAsync()
    {
        var jwt = await HentJwtAsync();
        if (jwt is null) return null;
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return http;
    }

    // ---- Click-to-call -------------------------------------------------------------------

    public record RingeResultat(bool Ok, int Kode, string Melding, string? Zid);

    /// <summary>Setter opp et utgående anrop: Zisson ringer agentens telefon og kobler til
    /// <paramref name="tilNummer"/>. callerId er utgående visningsnummer (valgfritt).</summary>
    public async Task<RingeResultat> ClickToCallAsync(string agentGuid, string tilNummer, string? callerId = null)
    {
        if (string.IsNullOrWhiteSpace(agentGuid)) return new(false, -1, "Agenten er ikke koblet til Zisson (mangler agent-guid).", null);
        var normalisert = NormaliserNummer(tilNummer);
        if (normalisert is null) return new(false, -1, "Ugyldig nummer.", null);

        var http = await KlientAsync();
        if (http is null) return new(false, -1, "Zisson er ikke konfigurert (mangler token/credentials).", null);

        // Agent-koblingen kan være lagret som brukernavn (f.eks. «3657») i stedet for guid –
        // slå opp guid fra Zisson-brukerlista når verdien ikke allerede er en guid.
        var løstAgentGuid = await LøsAgentGuidAsync(agentGuid);

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                agentGuid = løstAgentGuid,
                toNumber = normalisert,
                autoAnswer = await AutoAnswerAsync(),
                callerId = string.IsNullOrWhiteSpace(callerId) ? await DefaultCallerIdAsync() : callerId,
            });
            using var resp = await http.PostAsync("/external-api/v1/external-agent/click-to-call",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new(false, (int)resp.StatusCode, TolkFeil(txt, (int)resp.StatusCode), null);

            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            var kode = root.TryGetProperty("responseCode", out var rc) && rc.TryGetInt32(out var k) ? k : 5;
            var zid = root.TryGetProperty("zid", out var z) ? z.GetString() : null;
            var melding = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            return kode == 0
                ? new(true, 0, "Anrop satt opp – agentens telefon ringer.", zid)
                : new(false, kode, TolkKode(kode, melding), zid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Zisson click-to-call feilet");
            return new(false, -1, "Teknisk feil ved oppringing.", null);
        }
    }

    // AgentApiResponseCode → norsk melding.
    private static string TolkKode(int kode, string? melding) => kode switch
    {
        1 => "Agenten ble ikke funnet i Zisson.",
        2 => "Ugyldig nummer.",
        3 => "Agenten er opptatt eller ikke pålogget/klar i Zisson.",
        4 => "Tidsavbrudd mot Zisson – prøv igjen.",
        7 => "Ugyldig visningsnummer (callerId).",
        _ => string.IsNullOrWhiteSpace(melding) ? "Ukjent feil fra Zisson." : melding!,
    };

    private static string TolkFeil(string txt, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } s) return s;
            if (doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s2) return s2;
        }
        catch { /* ikke JSON */ }
        return status == 401 ? "Ikke autorisert mot Zisson (sjekk credentials)." : $"Feil fra Zisson (HTTP {status}).";
    }

    // Løser en agent-verdi til guid: er den allerede en guid returneres den som den er;
    // ellers tolkes den som brukernavn og slås opp mot Zisson-brukerlista. Faller tilbake til
    // råverdien hvis oppslag ikke gir treff (best effort).
    private async Task<string> LøsAgentGuidAsync(string agent)
    {
        if (Guid.TryParse(agent, out _)) return agent;
        try
        {
            var agenter = await HentAgenterAsync();
            var treff = agenter.FirstOrDefault(a => string.Equals(a.Username, agent, StringComparison.OrdinalIgnoreCase))
                     ?? agenter.FirstOrDefault(a => string.Equals(a.Navn, agent, StringComparison.OrdinalIgnoreCase));
            return treff?.Guid ?? agent;
        }
        catch { return agent; }
    }

    // ---- Oppslag (agenter, visningsnumre) ------------------------------------------------

    public record ZAgent(string Guid, string Navn, string? Username, string? Mobil);
    public record ZNummer(string Nummer, string? Navn);

    public async Task<List<ZAgent>> HentAgenterAsync()
    {
        var http = await KlientAsync();
        if (http is null) return new();
        try
        {
            using var resp = await http.GetAsync("/external-api/v1/entities/users");
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var liste = new List<ZAgent>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var guid = e.TryGetProperty("guid", out var g) ? g.GetString() : null;
                if (string.IsNullOrWhiteSpace(guid)) continue;
                var fornavn = e.TryGetProperty("firstName", out var f) ? f.GetString() : "";
                var etternavn = e.TryGetProperty("lastName", out var l) ? l.GetString() : "";
                var user = e.TryGetProperty("username", out var u) ? u.GetString() : null;
                var mobil = e.TryGetProperty("mobileNumber", out var mo) ? mo.GetString() : null;
                var navn = $"{fornavn} {etternavn}".Trim();
                liste.Add(new(guid!, string.IsNullOrWhiteSpace(navn) ? (user ?? guid!) : navn, user, mobil));
            }
            return liste.OrderBy(a => a.Navn, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av Zisson-agenter feilet"); return new(); }
    }

    public async Task<List<ZNummer>> HentVisningsnumreAsync()
    {
        var http = await KlientAsync();
        if (http is null) return new();
        try
        {
            using var resp = await http.GetAsync("/external-api/v1/entities/service-numbers");
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var liste = new List<ZNummer>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var nr = e.TryGetProperty("number", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(nr)) continue;
                var navn = e.TryGetProperty("name", out var na) ? na.GetString() : null;
                liste.Add(new(nr!, navn));
            }
            return liste;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av visningsnumre feilet"); return new(); }
    }

    // ---- CDR / utfall (brukes av ZissonCdrWorker) ---------------------------------------

    public record SamtaleUtfall(bool Funnet, bool Avsluttet, bool Svart, int TaletidSek);

    /// <summary>Slår opp utfallet for en samtale (zid). Bruker ConversationSessions i et
    /// tidsvindu og matcher på conversationId. Svart ≈ samtalen har en avslutning med varighet.</summary>
    public async Task<SamtaleUtfall> HentUtfallAsync(string zid, DateTime fra, DateTime til)
    {
        var http = await KlientAsync();
        if (http is null || string.IsNullOrWhiteSpace(zid)) return new(false, false, false, 0);
        try
        {
            var q = $"/external-api/v1/external-statdb/ConversationSessions?from={Uri.EscapeDataString(fra.ToString("o"))}&to={Uri.EscapeDataString(til.ToString("o"))}";
            using var resp = await http.GetAsync(q);
            if (!resp.IsSuccessStatusCode) return new(false, false, false, 0);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var cid = e.TryGetProperty("conversationId", out var c) ? c.GetString() : null;
                if (!string.Equals(cid, zid, StringComparison.OrdinalIgnoreCase)) continue;

                DateTime? start = LesTid(e, "conversationStartTimestamp");
                DateTime? slutt = LesTid(e, "conversationEndTimestamp");
                if (slutt is null) return new(true, false, false, 0); // pågår fortsatt
                var taletid = start is not null ? Math.Max(0, (int)(slutt.Value - start.Value).TotalSeconds) : 0;
                var svart = taletid >= 3; // heuristikk: kort «samtale» = ikke besvart
                return new(true, true, svart, taletid);
            }
            return new(false, false, false, 0); // ikke dukket opp i CDR ennå
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av samtale-utfall feilet"); return new(false, false, false, 0); }
    }

    private static DateTime? LesTid(JsonElement e, string navn)
        => e.TryGetProperty(navn, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var t)
            ? t.ToUniversalTime() : null;
}
