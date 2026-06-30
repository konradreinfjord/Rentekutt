using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// LinkMobility MyLink SMS API v1 (https://docs.linkmobility.com/api-reference/mylink-sms-api).
/// OAuth2 client credentials → access token (cachet) → send SMS. Brukes til 2FA-koder.
///
/// Hemmeligheter leses KUN fra config (Azure App Settings / user-secrets), aldri fra delt DB.
/// Inert til Client ID + secret er satt.
///
/// Azure-config (dobbel understrek):
///   LinkMobility__ClientId, LinkMobility__ClientSecret
///   LinkMobility__Sender (valgfri, std "Rentekutt")
///   LinkMobility__TokenUrl, LinkMobility__SendUrl (overstyrbare)
/// </summary>
public class LinkMobilityService
{
    private const string StandardSender = "Rentekutt";
    private const string StandardTokenUrl = "https://sso.linkmobility.com/auth/realms/CPaaS/protocol/openid-connect/token";
    private const string StandardSendUrl = "https://api.linkmobility.com/sms/v1/messages";

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LinkMobilityService> _logger;

    public LinkMobilityService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<LinkMobilityService> logger)
    {
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private string? ClientId => _config["LinkMobility:ClientId"];
    private string? ClientSecret => _config["LinkMobility:ClientSecret"];
    // Ikke-hemmelige felt — kan vises i Admin.
    public string Sender => _config["LinkMobility:Sender"] ?? StandardSender;
    public string TokenUrl => _config["LinkMobility:TokenUrl"] ?? StandardTokenUrl;
    public string SendUrl => _config["LinkMobility:SendUrl"] ?? StandardSendUrl;

    /// <summary>True når Client ID + secret er satt. Da kan vi sende SMS / aktivere 2FA.</summary>
    public bool ErKonfigurert =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    private string? _token;
    private DateTime _tokenUtloper = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private async Task<(string? Token, string? Feil)> HentTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenUtloper.AddSeconds(-30)) return (_token, null);
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenUtloper.AddSeconds(-30)) return (_token, null);
            var http = _httpFactory.CreateClient("linkmobility");
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = ClientId!,
                    ["client_secret"] = ClientSecret!,
                }),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("LinkMobility token feilet: HTTP {S}", (int)resp.StatusCode);
                return (null, $"token-endepunkt svarte HTTP {(int)resp.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var sek = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3000;
            _tokenUtloper = DateTime.UtcNow.AddSeconds(sek);
            return string.IsNullOrEmpty(_token) ? (null, "token-svar manglet access_token") : (_token, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LinkMobility token-oppslag feilet");
            return (null, ex.Message);
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>Send én SMS. Returnerer (ok, http-status, svar/feil).</summary>
    public async Task<(bool Ok, int Status, string Detalj)> SendSmsAsync(string mobil, string melding, CancellationToken ct = default)
    {
        if (!ErKonfigurert) return (false, 0, "LinkMobility ikke konfigurert (Client ID/secret mangler).");
        var mottaker = NormaliserMobil(mobil);
        if (string.IsNullOrEmpty(mottaker)) return (false, 0, "Ugyldig mobilnummer.");
        try
        {
            var (token, tokenFeil) = await HentTokenAsync(ct);
            if (string.IsNullOrEmpty(token)) return (false, 401, $"Klarte ikke hente access token: {tokenFeil}");

            var http = _httpFactory.CreateClient("linkmobility");
            var payload = new[]
            {
                new
                {
                    recipient = mottaker,
                    content = new
                    {
                        text = melding,
                        options = new Dictionary<string, string>
                        {
                            ["sms.encoding"] = "AutoDetect",
                            ["sms.sender"] = Sender,
                        },
                    },
                    referenceId = Guid.NewGuid().ToString(),
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, SendUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) _logger.LogWarning("LinkMobility SMS feilet: HTTP {S}", (int)resp.StatusCode);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LinkMobility SMS-sending feilet");
            return (false, 0, ex.Message);
        }
    }

    /// <summary>Normaliserer til E.164: 8 siffer → +47, 00-prefiks → +, ellers behold + og siffer.</summary>
    public static string NormaliserMobil(string? mobil)
    {
        var raw = (mobil ?? "").Trim();
        if (raw.StartsWith("00")) raw = "+" + raw[2..];
        var plus = raw.StartsWith("+");
        var siffer = new string(raw.Where(char.IsDigit).ToArray());
        if (siffer.Length == 0) return "";
        if (plus) return "+" + siffer;
        return siffer.Length == 8 ? "+47" + siffer : "+" + siffer;
    }
}
