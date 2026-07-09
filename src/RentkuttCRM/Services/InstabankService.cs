using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>True hvis banknavnet er Instabank (uansett formatering).</summary>
    public static bool ErInstabankNavn(string? navn) =>
        !string.IsNullOrWhiteSpace(navn) && navn.Replace(" ", "").Contains("instabank", StringComparison.OrdinalIgnoreCase);

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

    // Instabank vil IKKE ha tomme strenger / 0 / false for felt uten verdi — utelat dem.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<Resultat> PostAsync(string operasjon, object body)
    {
        var env = await MiljoAsync();
        var http = LagKlient(env);
        if (http is null) return new(false, null, null, null, "Instabank er ikke konfigurert (mangler brukernavn/passord).");
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
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

    /// <summary>
    /// Send en delegert forbrukslånssøknad (produkt 151) til Instabank.
    /// Feltene følger det verifiserte Agent API-skjemaet: EMail / MobilePhoneNumber,
    /// enum-verdier for sivilstatus/arbeid/gjeldstype, og tomme/0-verdier utelates.
    /// </summary>
    public async Task<Resultat> SendSoknadAsync(Kundekort k, bool preOffer = false)
    {
        // Bedriftslån (2001) har eget skjema (Company/Agent/PurposeForLoan) som ikke er verifisert her.
        if (k.KundeType == "B2B")
            return new(false, null, null, null, "Bedriftslån til Instabank er ikke ferdig mappet ennå — kun personlån (151) sendes foreløpig.");

        // Påkrevde felt: SSN, e-post, mobil, beløp.
        var ssn = FoerstGyldigFnr(k.Foedselsnummer, k.KundeId);
        var mangler = new List<string>();
        if (string.IsNullOrWhiteSpace(ssn)) mangler.Add("fødselsnummer");
        if (string.IsNullOrWhiteSpace(k.Epost)) mangler.Add("e-post");
        if (string.IsNullOrWhiteSpace(k.Mobilnummer)) mangler.Add("mobilnummer");
        if ((k.OnsketLaanebelop ?? 0) <= 0) mangler.Add("ønsket lånebeløp");
        if (mangler.Count > 0)
            return new(false, null, null, null, "Kan ikke sende — mangler: " + string.Join(", ", mangler));

        // Applicant — kun felt med reell verdi (utelat null/0/false).
        var applicant = new Dictionary<string, object?>
        {
            ["SocialSecurityNumber"] = ssn,
            ["EMail"] = k.Epost!.Trim(),
            ["MobilePhoneNumber"] = new string((k.Mobilnummer ?? "").Where(char.IsDigit).ToArray()),
        };
        Legg(applicant, "MaritalStatus", MapSivilstatus(k.Sivilstatus));
        Legg(applicant, "EmploymentStatus", MapArbeid(k.Arbeidssituasjon));
        if (k.AntallBarnUnder18 is int barn && barn >= 0) applicant["NumberOfChildren"] = barn;
        var eier = EierBolig(k.Boforhold);
        if (eier is not null) applicant["OwnsHouse"] = eier.Value;
        if (k.AntallBiler is int biler && biler > 0) applicant["OwnsCar"] = true;
        if (eier == false && k.BoligkostnadMnd is > 0) applicant["MonthlyRent"] = k.BoligkostnadMnd;
        if (ErNorsk(k.Statsborgerskap)) applicant["IsCitizen"] = true;
        if (k.AarsinntektBrutto is > 0) applicant["YearlyIncome"] = k.AarsinntektBrutto;
        if (k.AndreInntekter is > 0) applicant["YearlyIncomeOther"] = k.AndreInntekter;
        if (k.EktefelleInntekt is > 0) applicant["YearlyIncomeSpouse"] = k.EktefelleInntekt;

        // DebtDetails — kun sikret gjeld har enum-type (usikret forbruksgjeld dekkes av RefinanceAmount).
        var gjeld = new List<object>();
        if (k.Boliggjeld is > 0)
            gjeld.Add(RensNull(new Dictionary<string, object?> { ["Type"] = 1, ["Amount"] = k.Boliggjeld, ["Interest"] = k.NaavaerendeRente is > 0 ? k.NaavaerendeRente : null }));
        if (k.Studielaan is > 0) gjeld.Add(new Dictionary<string, object?> { ["Type"] = 2, ["Amount"] = k.Studielaan });
        if (k.Billaan is > 0) gjeld.Add(new Dictionary<string, object?> { ["Type"] = 3, ["Amount"] = k.Billaan });
        if (gjeld.Count > 0) applicant["DebtDetails"] = gjeld;

        var application = new Dictionary<string, object?>
        {
            ["Product"] = new { Code = ProduktForbrukslaan },
            ["Calculation"] = k.OnsketLopetidMnd is int lm && lm > 0
                ? new Dictionary<string, object?> { ["Amount"] = k.OnsketLaanebelop, ["DurationInMonths"] = lm }
                : new Dictionary<string, object?> { ["Amount"] = k.OnsketLaanebelop },
            ["Applicant"] = applicant,
            ["IsPreOffer"] = preOffer,
            ["Reference"] = k.Id.ToString(),
        };
        if (k.RefinansieresBelop is > 0) application["RefinanceAmount"] = k.RefinansieresBelop;

        return await PostAsync("create", new { Application = application, DoSetAccepted = false });
    }

    private static void Legg(Dictionary<string, object?> d, string nokkel, int? verdi)
    {
        if (verdi is not null) d[nokkel] = verdi.Value;
    }

    private static Dictionary<string, object?> RensNull(Dictionary<string, object?> d)
    {
        foreach (var k in d.Keys.ToList()) if (d[k] is null) d.Remove(k);
        return d;
    }

    private static string? FoerstGyldigFnr(params string?[] kandidater) =>
        kandidater.Select(x => new string((x ?? "").Where(char.IsDigit).ToArray()))
                  .FirstOrDefault(x => x.Length == 11);

    // MaritalStatus: 1 Married, 2 Cohabiting, 3 Divorced, 4 Single.
    private static int? MapSivilstatus(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "gift" => 1,
        "samboer" => 2,
        "skilt" or "separert" => 3,
        "singel" => 4,
        var x when x.StartsWith("enke") => 4,
        _ => null,
    };

    // EmploymentStatus: 1 Fast, 2 Midlertidig, 3 Selvstendig, 4 Arbeidsledig, 5 Sykepenger,
    // 6 Uføretrygd, 7 Pensjonist, 8 Student, 9 Annet.
    private static int? MapArbeid(string? s)
    {
        var x = (s ?? "").ToLowerInvariant();
        if (x.Contains("fast") || x.Contains("offentlig")) return 1;
        if (x.Contains("midlertid") || x.Contains("vikar")) return 2;
        if (x.Contains("selvstend") || x.Contains("næring")) return 3;
        if (x.Contains("arbeidsled")) return 4;
        if (x.Contains("ufør")) return 6;
        if (x.Contains("pensjon")) return 7;
        if (x.Contains("student")) return 8;
        if (x.Contains("hjemme")) return 9;
        return null;
    }

    private static bool? EierBolig(string? boforhold)
    {
        var x = (boforhold ?? "").ToLowerInvariant();
        if (x.Length == 0) return null;
        if (x.Contains("selveier") || x.Contains("eier") || x.Contains("andel") || x.Contains("borettslag")) return true;
        if (x.Contains("leier") || x.Contains("foreldre")) return false;
        return null;
    }

    private static bool ErNorsk(string? statsborgerskap) =>
        (statsborgerskap ?? "").Trim().ToLowerInvariant() is "norsk" or "norge" or "no";

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
