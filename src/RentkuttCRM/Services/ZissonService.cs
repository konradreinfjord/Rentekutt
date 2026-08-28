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
    private const string CustomerGuidKey = "zisson_customer_guid";
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
    private string? LoginId => _config["Zisson:Id"];
    private string? RefreshTokenConfig => _config["Zisson:RefreshToken"];

    /// <summary>Kundens Zisson-guid (customerGuid). Kan settes i Dialer-fanen (lagres i innstillinger)
    /// og faller ellers tilbake til appsettings.</summary>
    public async Task<string?> CustomerGuidAsync()
        => (await _settings.GetAsync(CustomerGuidKey)) is { Length: > 0 } s ? s : _config["Zisson:CustomerGuid"];
    public Task SettCustomerGuidAsync(string? verdi)
        => _settings.SetAsync(CustomerGuidKey, string.IsNullOrWhiteSpace(verdi) ? null : verdi.Trim());

    public async Task<bool> ErKonfigurertAsync()
        => !string.IsNullOrWhiteSpace(await CustomerGuidAsync())
           && !string.IsNullOrWhiteSpace(LoginId)
           && !string.IsNullOrWhiteSpace(await GjeldendeRefreshTokenAsync());

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
            var customerGuid = await CustomerGuidAsync();
            if (string.IsNullOrWhiteSpace(customerGuid) || string.IsNullOrWhiteSpace(LoginId) || string.IsNullOrWhiteSpace(refreshToken))
                return null;

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            var body = JsonSerializer.Serialize(new
            {
                customerGuid,
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

        // Zisson krever en gyldig guid i agentGuid. Klarte vi ikke å løse brukernavnet til en guid,
        // stopper vi her med en tydelig melding i stedet for et kryptisk 400-valideringssvar.
        if (!Guid.TryParse(løstAgentGuid, out _))
            return new(false, -1, $"Fant ikke Zisson-agenten «{agentGuid}». Lim inn agentens guid, eller velg fra nedtrekkslista når agentlista er tilgjengelig.", null);

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

            // Suksess-status, men tomt/ikke-JSON svar → ikke krasj; rapporter det vi fikk.
            if (string.IsNullOrWhiteSpace(txt))
                return new(true, 0, "Anrop satt opp (Zisson svarte uten innhold).", null);

            JsonElement root;
            try { root = JsonDocument.Parse(txt).RootElement; }
            catch
            {
                var kort = txt.Length > 120 ? txt[..120] : txt;
                return new(false, -2, $"Uventet svar fra Zisson (HTTP {(int)resp.StatusCode}): {kort}", null);
            }
            // responseCode kan komme som tall (0) ELLER tekst («Ok»/«0»/«AgentNotFound») – tål begge.
            var kode = LesResponseCode(root);
            var zid = LesStreng(root, "zid");
            var melding = LesStreng(root, "message");
            return kode == 0
                ? new(true, 0, "Anrop satt opp – agentens telefon ringer.", zid)
                : new(false, kode, TolkKode(kode, melding), zid);
        }
        catch (TaskCanceledException)
        {
            _log.LogError("Zisson click-to-call tidsavbrudd");
            return new(false, -1, "Tidsavbrudd mot Zisson (svarte ikke innen 20 s).", null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Zisson click-to-call feilet");
            var indre = ex.InnerException is { } ie ? $" ({ie.GetType().Name}: {ie.Message})" : "";
            return new(false, -1, $"Teknisk feil ved oppringing: {ex.GetType().Name}: {ex.Message}{indre}", null);
        }
    }

    // Leser en JSON-verdi som streng uansett om den er tekst/tall/annet (unngår GetString()-krasj).
    private static string? LesStreng(JsonElement root, string navn)
    {
        if (!root.TryGetProperty(navn, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }

    // Leser responseCode uansett om Zisson sender tall (0) eller tekst («0» / «Ok» / «AgentNotFound»).
    private static int LesResponseCode(JsonElement root)
    {
        if (!root.TryGetProperty("responseCode", out var rc)) return 5;
        if (rc.ValueKind == JsonValueKind.Number) return rc.TryGetInt32(out var n) ? n : 5;
        var s = rc.ValueKind == JsonValueKind.String ? rc.GetString() : rc.GetRawText();
        if (int.TryParse(s, out var k)) return k;
        return (s ?? "").Trim().ToLowerInvariant() switch
        {
            "ok" => 0,
            "agentnotfound" => 1,
            "invalidnumber" => 2,
            "agentcallstateerror" => 3,
            "timeout" => 4,
            "notimplemented" => 6,
            "invalidcallerid" => 7,
            _ => 5, // UnknownError
        };
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
        if (status == 404)
            return "404 fra Zisson – fant ikke agenten/ressursen. Enten er agent-guid-en ikke en gyldig agent, eller så er ikke click-to-call/External-API aktivert for kontoen.";
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            // ASP.NET valideringsfeil: {"errors":{"felt":["melding", …]}} – vis feltnavn + melding.
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
            {
                var deler = new List<string>();
                foreach (var felt in errs.EnumerateObject())
                {
                    var meldinger = felt.Value.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", felt.Value.EnumerateArray().Select(v => v.GetString()))
                        : felt.Value.GetString();
                    deler.Add($"{felt.Name}: {meldinger}");
                }
                if (deler.Count > 0) return "Zisson avviste forespørselen – " + string.Join("; ", deler);
            }
            if (root.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } s) return s;
            if (root.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s2) return s2;
        }
        catch { /* ikke JSON */ }
        return status == 401 ? "Ikke autorisert mot Zisson (sjekk credentials)." : $"Feil fra Zisson (HTTP {status}).";
    }

    // Løser en agent-verdi til guid: er den allerede en guid returneres den som den er;
    // ellers tolkes den som bruker-id/brukernavn (f.eks. «3657») og slås opp mot Zisson-brukerlista
    // (entities/users + id-mapping). Faller tilbake til råverdien hvis oppslag ikke gir treff.
    private async Task<string> LøsAgentGuidAsync(string agent)
    {
        if (Guid.TryParse(agent, out _)) return agent;
        try
        {
            var agenter = await HentAgenterAlleAsync();
            var treff = agenter.FirstOrDefault(a => string.Equals(a.LoginId, agent, StringComparison.OrdinalIgnoreCase))
                     ?? agenter.FirstOrDefault(a => string.Equals(a.Username, agent, StringComparison.OrdinalIgnoreCase))
                     ?? agenter.FirstOrDefault(a => string.Equals(a.Navn, agent, StringComparison.OrdinalIgnoreCase));
            return treff?.Guid ?? agent;
        }
        catch { return agent; }
    }

    // ---- Oppslag (agenter, visningsnumre) ------------------------------------------------

    public record ZAgent(string Guid, string Navn, string? Username, string? Mobil, string? LoginId = null);
    public record ZNummer(string Nummer, string? Navn);

    /// <summary>Agenter fra entities/users (guid, navn, brukernavn, mobil).</summary>
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

    /// <summary>Id-mapping (entities/users/id-mapping): kobler bruker-id (tall, f.eks. 3657) ↔ guid.</summary>
    public async Task<List<ZAgent>> HentIdMappingAsync()
    {
        var http = await KlientAsync();
        if (http is null) return new();
        try
        {
            using var resp = await http.GetAsync("/external-api/v1/entities/users/id-mapping");
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var liste = new List<ZAgent>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var guid = e.TryGetProperty("guid", out var g) ? g.GetString() : null;
                if (string.IsNullOrWhiteSpace(guid)) continue;
                var loginId = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32().ToString() : null;
                var user = e.TryGetProperty("username", out var u) ? u.GetString() : null;
                var fornavn = e.TryGetProperty("firstName", out var f) ? f.GetString() : "";
                var etternavn = e.TryGetProperty("lastName", out var l) ? l.GetString() : "";
                var navn = $"{fornavn} {etternavn}".Trim();
                liste.Add(new(guid!, string.IsNullOrWhiteSpace(navn) ? (user ?? loginId ?? guid!) : navn, user, null, loginId));
            }
            return liste;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av id-mapping feilet"); return new(); }
    }

    /// <summary>Slår sammen entities/users og id-mapping (ett kan være tomt avhengig av tilganger).
    /// Nøkkel på guid; beholder LoginId fra id-mapping når tilgjengelig.</summary>
    public async Task<List<ZAgent>> HentAgenterAlleAsync()
    {
        var users = await HentAgenterAsync();
        var mapping = await HentIdMappingAsync();
        var byGuid = new Dictionary<string, ZAgent>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in users) byGuid[a.Guid] = a;
        foreach (var m in mapping)
            byGuid[m.Guid] = byGuid.TryGetValue(m.Guid, out var u)
                ? u with { LoginId = u.LoginId ?? m.LoginId, Username = u.Username ?? m.Username }
                : m;
        return byGuid.Values.OrderBy(a => a.Navn, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    /// <summary>Diagnostikk: viser faktisk HTTP-status per External-API-endepunkt, så vi skiller
    /// «404 = ikke tilgjengelig for kontoen» fra «200 = tomt». Skriver ingen data.</summary>
    public async Task<string> DiagnostikkAsync()
    {
        var jwt = await HentJwtAsync();
        if (jwt is null) return "Token: FEIL — fikk ikke JWT (sjekk customerGuid/id/refreshToken).";
        var http = await KlientAsync();
        if (http is null) return "Token OK, men kunne ikke opprette HTTP-klient.";

        var deler = new List<string> { "Token: OK" };
        var endepunkter = new (string Navn, string Sti)[]
        {
            ("entities/users", "/external-api/v1/entities/users"),
            ("id-mapping", "/external-api/v1/entities/users/id-mapping"),
            ("service-numbers", "/external-api/v1/entities/service-numbers"),
        };
        foreach (var (navn, sti) in endepunkter)
        {
            try
            {
                using var resp = await http.GetAsync(sti);
                var status = (int)resp.StatusCode;
                var suffiks = "";
                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.ValueKind == JsonValueKind.Array) suffiks = $" ({doc.RootElement.GetArrayLength()} rader)";
                    }
                    catch { /* ikke array */ }
                }
                deler.Add($"{navn}: HTTP {status}{suffiks}");
            }
            catch (Exception ex) { deler.Add($"{navn}: feil ({ex.GetType().Name})"); }
        }
        return string.Join(" · ", deler);
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
