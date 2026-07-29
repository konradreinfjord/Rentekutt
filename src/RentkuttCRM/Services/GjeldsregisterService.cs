using System.Security.Cryptography.X509Certificates;

namespace RentkuttCRM.Services;

/// <summary>
/// Oppslag mot Gjeldsregisteret (via Infotorg) for søkers usikrede gjeld. Autentisering er
/// mutual-TLS med virksomhetssertifikat (Buypass/Commfides) — ikke bearer-token. Oppslag gjøres
/// KUN på fnr fra kundekort med gyldig 2FA-samtykke (teknisk sperre i <see cref="SlaaOppGjeldAsync"/>).
/// Miljø (test/prod) og AV/PÅ styres i Admin.
///
/// Config (Azure app settings):
///   Gjeldsregisteret__ApiUrlTest   = https://ws-test.infotorg.no/…   (valgfri; standard under)
///   Gjeldsregisteret__ApiUrlProd   = https://ws.infotorg.no/…        (valgfri; standard under)
///   Gjeldsregisteret__CertPath     = sti til virksomhetssertifikat (.p12/.pfx)
///   Gjeldsregisteret__CertPassword = sertifikatpassord
/// </summary>
public class GjeldsregisterService
{
    // Infotorg-endepunkter (kan overstyres i config). Prod-URL bekreftes mot avtalen.
    private const string StandardTest = "https://ws-test.infotorg.no";
    private const string StandardProd = "https://ws.infotorg.no";

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
    private readonly ILogger<GjeldsregisterService> _log;

    public GjeldsregisterService(IConfiguration config, SettingsService settings, SamtykkeService samtykke,
        LoggService logg, ILogger<GjeldsregisterService> log)
    {
        _config = config;
        _settings = settings;
        _samtykke = samtykke;
        _logg = logg;
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

    public string BaseUrl(string env) => env == "prod"
        ? (_config["Gjeldsregisteret:ApiUrlProd"] ?? StandardProd)
        : (_config["Gjeldsregisteret:ApiUrlTest"] ?? StandardTest);

    private string? CertBase64 => _config["Gjeldsregisteret:CertBase64"];
    private string? CertPath => _config["Gjeldsregisteret:CertPath"];
    private string? CertPassword => _config["Gjeldsregisteret:CertPassword"];

    /// <summary>Virksomhetssertifikatet er tilgjengelig og lastbart (mTLS mulig).</summary>
    public bool ErKonfigurert => LastSertifikat() is not null;

    /// <summary>Last Buypass-virksomhetssertifikatet (.p12/.pfx). I Azure legges det inn base64-kodet i
    /// app-innstillingen Gjeldsregisteret__CertBase64 (anbefalt) — alternativt en filsti via __CertPath.</summary>
    private X509Certificate2? LastSertifikat()
    {
        try
        {
            // .NET 10: X509CertificateLoader (ikke-obsolete) laster PKCS#12.
            if (!string.IsNullOrWhiteSpace(CertBase64))
                return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(CertBase64.Trim()), CertPassword);
            if (!string.IsNullOrWhiteSpace(CertPath) && File.Exists(CertPath))
                return X509CertificateLoader.LoadPkcs12FromFile(CertPath, CertPassword);
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke laste virksomhetssertifikat for Gjeldsregisteret"); return null; }
    }

    public record GjeldResultat(bool Ok, bool Avvist, string Melding, decimal? UsikretGjeld = null);

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
            return new(false, false, $"Venter på virksomhetssertifikat (mTLS) for {(env == "prod" ? "produksjon" : "test")}. Sett Gjeldsregisteret__CertPath/CertPassword når sertifikatet er mottatt.");

        // 4) Rate-grense (redigerbar i Admin).
        var rateFeil = await RateLimitAsync();
        if (rateFeil is not null)
        {
            await _logg.LoggAsync(k.Id, aktor, $"Gjeldsregister-oppslag stoppet av rate-grense: {rateFeil}", kategori: "avgjørelse");
            return new(false, false, rateFeil);
        }

        // 5) mTLS-kall mot Infotorg — request/response-mapping wires når søkeskjemaet (Standards Spec) er bekreftet.
        //    Struktur er klar: HttpClientHandler med ClientCertificates = { sertifikatet }, POST fnr → usikret gjeld.
        await _logg.LoggAsync(k.Id, aktor, $"Gjeldsregister-oppslag ({env}) — samtykke OK, klart for kall.", kategori: "kobling");
        return new(false, false, "Klar til oppslag (samtykke + sertifikat OK). Selve Infotorg-kallet aktiveres når søkeskjemaet er bekreftet.");
    }
}
