using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Instabank Agent API (Norge) — https://documenter.getpostman.com/view/37624662/2sAXqnfQ6U
///
/// For meglere som sender lånesøknader på vegne av kunder. Basic Auth, JSON over POST.
/// Alle kall: POST {base}/public/api/application/{operasjon} (create, get, setaccepted …).
/// Respons inneholder bl.a. ExternalReference, Status og SigningUrl (sendes til kunden).
///
/// Hemmeligheter leses KUN fra server-config (Azure App Settings / user-secrets), aldri fra delt DB:
///   Instabank__Username, Instabank__PasswordTest, Instabank__PasswordProd, Instabank__AgentEmail (valgfri)
/// Miljø (test/prod) og på/av styres fra Admin (lagres i innstillinger).
/// </summary>
public class InstabankService
{
    // Produktkoder fra Instabank Agent API.
    public const int ProduktForbrukslaan = 151;
    public const int ProduktKredittlinje = 251;
    public const int ProduktKredittkort = 600;
    public const int ProduktBedriftslaan = 2001;
    public const int ProduktBedriftKreditt = 2000;

    private const string ProdHost = "https://netbank.instabank.no";
    private const string TestHost = "https://netbankpp.instabank.no";
    private const string EnvKey = "instabank_env";        // "test" | "prod"
    private const string EnabledKey = "instabank_enabled"; // "true" | "false"

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<InstabankService> _log;

    public InstabankService(IConfiguration config, IHttpClientFactory httpFactory,
        SettingsService settings, ILogger<InstabankService> log)
    {
        _config = config;
        _httpFactory = httpFactory;
        _settings = settings;
        _log = log;
    }

    private string? Username => _config["Instabank:Username"];
    private string? AgentEmail => _config["Instabank:AgentEmail"];
    private string? PassordFor(string env) =>
        env == "prod" ? _config["Instabank:PasswordProd"] : _config["Instabank:PasswordTest"];

    public async Task<string> MiljoAsync() => (await _settings.GetAsync(EnvKey)) == "prod" ? "prod" : "test";
    public async Task<bool> AktivertAsync() => (await _settings.GetAsync(EnabledKey)) == "true";
    public Task SettMiljoAsync(string env) => _settings.SetAsync(EnvKey, env == "prod" ? "prod" : "test");
    public Task SettAktivertAsync(bool på) => _settings.SetAsync(EnabledKey, på ? "true" : "false");

    public static string BaseUrl(string env) => env == "prod" ? ProdHost : TestHost;

    /// <summary>True når brukernavn + passord for gjeldende miljø er satt.</summary>
    public async Task<bool> ErKonfigurertAsync()
    {
        var env = await MiljoAsync();
        return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PassordFor(env));
    }

    private HttpClient? LagKlient(string env)
    {
        var user = Username;
        var pass = PassordFor(env);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return null;
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(BaseUrl(env));
        http.Timeout = TimeSpan.FromSeconds(20);
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
        return http;
    }

    public record Resultat(bool Ok, string? ExternalReference, string? SigningUrl, string? Status, string Detalj);

    private async Task<Resultat> PostAsync(string operasjon, object body)
    {
        var env = await MiljoAsync();
        var http = LagKlient(env);
        if (http is null) return new(false, null, null, null, "Instabank er ikke konfigurert (mangler brukernavn/passord).");
        try
        {
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"/public/api/application/{operasjon}", content);
            var tekst = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new(false, null, null, null, "401 — feil brukernavn/passord for valgt miljø.");

            string? extRef = null, signing = null, status = null;
            try
            {
                using var doc = JsonDocument.Parse(tekst);
                var root = doc.RootElement;
                signing = Finn(root, "SigningUrl");
                status = Finn(root, "Status");
                extRef = Finn(root, "ExternalReference");
            }
            catch { /* ikke-JSON respons */ }

            return new(resp.IsSuccessStatusCode, extRef, signing, status,
                resp.IsSuccessStatusCode ? "OK" : $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Kort(tekst)}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Instabank {Op} feilet", operasjon);
            return new(false, null, null, null, "Nettverksfeil: " + ex.Message);
        }
    }

    /// <summary>Lett tilkoblingssjekk: henter en ikke-eksisterende sak (ingen bivirkninger). 401 = feil passord.</summary>
    public async Task<(bool Ok, string Detalj)> TestTilkoblingAsync()
    {
        var r = await PostAsync("get", new { Application = new { ExternalReference = "rentekutt-conntest" } });
        // Alt annet enn 401/nettverksfeil betyr at vi når API-et og er autentisert.
        if (r.Detalj.StartsWith("401") || r.Detalj.StartsWith("Nettverksfeil") || r.Detalj.StartsWith("Instabank er ikke"))
            return (false, r.Detalj);
        return (true, "Tilkoblet og autentisert ✓");
    }

    /// <summary>Send en delegert søknad til Instabank (forbrukslån B2C / bedriftslån B2B).</summary>
    public async Task<Resultat> SendSoknadAsync(Kundekort k, bool preOffer = true)
    {
        var bedrift = k.KundeType == "B2B";
        var produkt = bedrift ? ProduktBedriftslaan : ProduktForbrukslaan;
        var applicant = new Dictionary<string, object?>
        {
            ["SocialSecurityNumber"] = k.Foedselsnummer ?? k.KundeId,
            ["Email"] = k.Epost,
            ["Phone"] = k.Mobilnummer,
        };
        var application = new Dictionary<string, object?>
        {
            ["Product"] = new { Code = produkt },
            ["Calculation"] = new { Amount = k.OnsketLaanebelop ?? 0, DurationInMonths = k.OnsketLopetidMnd },
            ["RefinanceAmount"] = k.RefinansieresBelop,
            ["Applicant"] = applicant,
            ["IsPreOffer"] = preOffer,
            ["Reference"] = k.Id.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(AgentEmail))
            application["Agent"] = new { Email = AgentEmail };
        if (bedrift && !string.IsNullOrWhiteSpace(k.Orgnr))
            application["Company"] = new { OrganizationNumber = k.Orgnr, Email = k.Epost, Phone = k.Mobilnummer };

        return await PostAsync("create", new { Application = application, DoSetAccepted = false });
    }

    /// <summary>Hent status på en tidligere innsendt sak.</summary>
    public Task<Resultat> HentStatusAsync(string externalReference) =>
        PostAsync("get", new { Application = new { ExternalReference = externalReference } });

    private static string? Finn(JsonElement el, string navn)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, navn, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    return p.Value.ToString();
                var barn = Finn(p.Value, navn);
                if (barn is not null) return barn;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var barn = Finn(item, navn);
                if (barn is not null) return barn;
            }
        }
        return null;
    }

    private static string Kort(string s) => s.Length <= 240 ? s : s[..240] + "…";
}
