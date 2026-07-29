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
    public const int ProduktBoliglaan = 180;
    public const int ProduktKredittlinje = 251;
    public const int ProduktKredittkort = 600;
    public const int ProduktBedriftslaan = 2001;
    public const int ProduktBedriftKreditt = 2000;

    /// <summary>Instabank sine faste produkter — én kilde til sannhet for både seed,
    /// selvhelbredende «ensure» og standard lånetype-kobling for auto-valg av produkt.</summary>
    public record StandardProdukt(string Navn, int Kode, string Segment, string Laanetyper);
    public static readonly StandardProdukt[] StandardProdukter =
    {
        new("Forbrukslån",     ProduktForbrukslaan,   "privat",  "Forbrukslån,Refinansiering"),
        new("Boliglån",        ProduktBoliglaan,      "privat",  "Boliglån"),
        new("Kredittlinje",    ProduktKredittlinje,   "privat",  ""),
        new("Kredittkort",     ProduktKredittkort,    "privat",  ""),
        new("Bedriftslån",     ProduktBedriftslaan,   "bedrift", ""),
        new("Bedriftskreditt", ProduktBedriftKreditt, "bedrift", ""),
    };

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
    /// <summary>Maks lånebeløp for forbrukslån (produkt 151) hos Instabank. Beløp over dette
    /// avvises med E_CREDIT_LIMIT_IS_ABOVE_AGR_LIMIT. Overstyres med Instabank__ForbrukslaanMaks;
    /// standard 500 000 kr (Instabanks avtalegrense for forbrukslån).</summary>
    private decimal ForbrukslaanMaks =>
        decimal.TryParse(_config["Instabank:ForbrukslaanMaks"], out var v) && v > 0 ? v : 500_000m;

    /// <summary>Maks lånebeløp for boliglån (produkt 180) hos Instabank. Overstyres med
    /// Instabank__BoliglaanMaks; standard 10 000 000 kr.</summary>
    private decimal BoliglaanMaks =>
        decimal.TryParse(_config["Instabank:BoliglaanMaks"], out var v) && v > 0 ? v : 10_000_000m;

    /// <summary>Maks lånebeløp for et Instabank-produkt (forbrukslån/boliglån). Brukes både av
    /// send-barrieren og av kundeportalen for å vise grensen. 0 = ingen kjent grense.</summary>
    public decimal MaksBelopFor(int? produkt) => produkt switch
    {
        ProduktForbrukslaan => ForbrukslaanMaks,
        ProduktBoliglaan => BoliglaanMaks,
        _ => 0m,
    };
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
    /// Ruter søknaden til riktig Instabank-produkt ut fra kundetype og lånetype:
    /// bedrift → bedriftslån (2001), privat boliglån → gates (matrikkel via Eiendomsverdi),
    /// ellers privat forbrukslån (151). Alle felt følger det verifiserte Agent API-skjemaet.
    /// </summary>
    public async Task<Resultat> SendSoknadAsync(Kundekort k, int? produktKode = null, bool preOffer = false)
    {
        // Eksplisitt produktvalg (fra kundekortet/regelen) styrer rutingen når satt.
        switch (produktKode)
        {
            case ProduktForbrukslaan:                                   // 151
                if (ErBoliglaan(k.Laanetype))
                    return await SendPrivatAsync(k, preOffer, ProduktBoliglaan);
                return await SendPrivatAsync(k, preOffer);
            case ProduktBoliglaan:                                      // 180 (samme payload som 151)
                return await SendPrivatAsync(k, preOffer, ProduktBoliglaan);
            case ProduktBedriftslaan:                                   // 2001
                return await SendBedriftAsync(k, preOffer);
            case ProduktKredittlinje:                                   // 251
            case ProduktKredittkort:                                    // 600
            case ProduktBedriftKreditt:                                 // 2000
                return new(false, null, null, null,
                    $"Produktet «{ProduktNavn(produktKode.Value)}» ({produktKode}) støttes ikke for automatisk sending ennå — verifisert Agent API-payload mangler.");
        }

        // Ingen eksplisitt produktkode: behold tidligere auto-ruting på kundetype.
        if (k.KundeType == "B2B") return await SendBedriftAsync(k, preOffer);
        if (ErBoliglaan(k.Laanetype))
            return await SendPrivatAsync(k, preOffer, ProduktBoliglaan);
        return await SendPrivatAsync(k, preOffer);
    }

    /// <summary>Navn på et Instabank-produkt ut fra API-koden.</summary>
    public static string ProduktNavn(int kode) => kode switch
    {
        ProduktForbrukslaan => "Forbrukslån",
        ProduktBoliglaan => "Boliglån",
        ProduktKredittlinje => "Kredittlinje",
        ProduktKredittkort => "Kredittkort",
        ProduktBedriftslaan => "Bedriftslån",
        ProduktBedriftKreditt => "Bedriftskreditt",
        _ => $"Produkt {kode}",
    };

    /// <summary>Produktkoder som faktisk kan sendes til Instabank i dag (verifisert payload).
    /// null = auto-ruting (bakoverkompatibelt). 151/2001 støttes; 251/600/2000 gjør ikke ennå.</summary>
    public static bool KanSendeProdukt(int? kode) => kode is null or ProduktForbrukslaan or ProduktBoliglaan or ProduktBedriftslaan;

    private static bool ErBoliglaan(string? laanetype) =>
        (laanetype ?? "").Contains("bolig", StringComparison.OrdinalIgnoreCase);

    // Privat forbrukslån (151) og boliglån (180) — samme payload-skjema, kun produktkoden skiller.
    private async Task<Resultat> SendPrivatAsync(Kundekort k, bool preOffer, int produkt = ProduktForbrukslaan)
    {
        // Påkrevde felt: SSN, e-post, mobil, beløp.
        var ssn = FoerstGyldigFnr(k.Foedselsnummer, k.KundeId);
        var mangler = new List<string>();
        if (string.IsNullOrWhiteSpace(ssn)) mangler.Add(FnrManglerGrunn(k.Foedselsnummer));
        if (string.IsNullOrWhiteSpace(k.Epost)) mangler.Add("e-post");
        if (string.IsNullOrWhiteSpace(k.Mobilnummer)) mangler.Add("mobilnummer");
        if ((k.OnsketLaanebelop ?? 0) <= 0) mangler.Add("ønsket lånebeløp");
        // Instabank krever løpetid — uten den velger Instabank selv en verdi som kan havne under
        // avtalens minimum (E_DURATION_IS_BELOW_AGR_LIMIT). Krev en gyldig løpetid før sending.
        if ((k.OnsketLopetidMnd ?? 0) <= 0) mangler.Add("ønsket nedbetalingstid (mnd)");
        if (mangler.Count > 0)
            return new(false, null, null, null, "Kan ikke sende — mangler: " + string.Join(", ", mangler));

        // Beløpsgrense per produkt: forbrukslån (151) opp til ForbrukslaanMaks, boliglån (180) opp til
        // BoliglaanMaks. Beløp over avvises av Instabank med E_CREDIT_LIMIT_IS_ABOVE_AGR_LIMIT (uleselig
        // 500-feil). Vi fanger det før sending og gir rådgiveren en tydelig melding — samme barriere for
        // manuell og automatisk sending.
        var maks = MaksBelopFor(produkt);
        if (maks > 0 && (k.OnsketLaanebelop ?? 0) > maks)
        {
            var pnavn = ProduktNavn(produkt).ToLowerInvariant();
            var tips = produkt == ProduktForbrukslaan ? " Så høye beløp er boliglån." : "";
            return new(false, null, null, null,
                $"Beløpet {k.OnsketLaanebelop:N0} kr overstiger maksbeløpet for {pnavn} hos Instabank ({maks:N0} kr).{tips}");
        }

        // Valider fødselsnummeret lokalt (modulus-11) før vi kaller Instabank — unngår
        // 500-feil «Invalid socialSecurityNumber» og beskytter bank-API-et mot ugyldige data.
        if (!ErGyldigFnr(ssn))
            return new(false, null, null, null, "Ugyldig fødselsnummer — ikke et gyldig norsk fødselsnummer (11 siffer, feil kontrollsiffer). Prismatch-leads mangler fnr; fyll inn et gyldig fødselsnummer på kundekortet før sending.");

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
        // OwnsCar/IsCitizen sendes EKSPLISITT true/false når vi kjenner verdien (Instabank etterlyser
        // dem — å utelate ved «false» leses som «mangler»). Er kilden ukjent (tom), utelates feltet.
        if (k.AntallBiler is int biler) applicant["OwnsCar"] = biler > 0;
        if (eier == false && k.BoligkostnadMnd is > 0) applicant["MonthlyRent"] = k.BoligkostnadMnd;
        if (!string.IsNullOrWhiteSpace(k.Statsborgerskap)) applicant["IsCitizen"] = ErNorsk(k.Statsborgerskap);
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
            ["Product"] = new { Code = produkt },
            ["Calculation"] = k.OnsketLopetidMnd is int lm && lm > 0
                ? new Dictionary<string, object?> { ["Amount"] = k.OnsketLaanebelop, ["DurationInMonths"] = lm }
                : new Dictionary<string, object?> { ["Amount"] = k.OnsketLaanebelop },
            ["Applicant"] = applicant,
            ["IsPreOffer"] = preOffer,
            ["Reference"] = k.Id.ToString(),
        };
        if (k.RefinansieresBelop is > 0) application["RefinanceAmount"] = k.RefinansieresBelop;

        // Boliglån (180) krever eiendoms-/sikkerhetsobjekt (Items[]). Bygges fra G · Boliglån-feltene.
        // «Debt» = restgjeld på eiendommen = Boliggjeld. Matrikkel for selveier; Cooperation for andel.
        if (produkt == ProduktBoliglaan)
        {
            static object? Tekst(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            var eiendom = RensNull(new Dictionary<string, object?>
            {
                ["Municipality"] = Tekst(k.EiendomKommune),
                ["MunicipalityNumber"] = k.EiendomKommunenummer,
                ["CadastralUnitNumber"] = k.EiendomGaardsnummer,
                ["UnitNumber"] = k.EiendomBruksnummer,
                // Festenummer 0 = «ingen feste» — send 0 når matrikkel oppgis (gnr satt), ellers utelates.
                ["LeaseholdUnitNumber"] = k.EiendomFestenummer ?? (k.EiendomGaardsnummer is not null ? 0 : (int?)null),
                ["SectionNumber"] = k.EiendomSeksjonsnummer,
                ["ApartmentReference"] = Tekst(k.EiendomAndelsnummer),
                ["Cooperation"] = string.IsNullOrWhiteSpace(k.EiendomBorettslagOrgnr) ? null
                    : new Dictionary<string, object?> { ["OrganizationNumber"] = new string(k.EiendomBorettslagOrgnr.Where(char.IsDigit).ToArray()) },
                ["CommonDebt"] = k.EiendomFellesgjeld,
                ["CommonCost"] = k.EiendomFelleskostnad,
                ["EstimatedValue"] = k.EiendomEstimertVerdi,
                ["EstimateReference"] = Tekst(k.EiendomEtakstReferanse),
                ["IsInsured"] = k.EiendomForsikret,
                ["InsuranceCompany"] = string.IsNullOrWhiteSpace(k.EiendomForsikringsselskap) ? null
                    : new Dictionary<string, object?> { ["Name"] = k.EiendomForsikringsselskap.Trim() },
                ["Debt"] = k.Boliggjeld,
            });
            // Items[] ligger rett under Application (bekreftet mot Instabank Agent API «Create mortgage»).
            application["Items"] = new[] { eiendom };
        }

        var r = await PostAsync("create", new { Application = application, DoSetAccepted = false });
        return r.Ok ? r : r with { Detalj = $"{r.Detalj} [sendt: beløp={k.OnsketLaanebelop:N0} kr, løpetid={(k.OnsketLopetidMnd?.ToString() ?? "ikke satt")} mnd]" };
    }

    // Bedriftslån (produkt 2001). Krever Company (orgnr + mobil), Applicant (signer-fnr) og Agent-e-post.
    private async Task<Resultat> SendBedriftAsync(Kundekort k, bool preOffer)
    {
        var orgnr = new string((k.Orgnr ?? k.KundeId ?? "").Where(char.IsDigit).ToArray());
        var ssn = FoerstGyldigFnr(k.Foedselsnummer);
        var mobil = new string((k.Mobilnummer ?? "").Where(char.IsDigit).ToArray());

        // Agent-e-post: den rådgiveren som EIER (og sender) saken — lagres på kundekortet
        // som Eier. Sending skjer i bakgrunnskøen uten innlogget sesjon, så Eier er den
        // eneste pålitelige kilden. Fall tilbake til den globale Instabank__AgentEmail-
        // konfigurasjonen for ueide saker.
        var agentEpost = ErEpost(k.Eier) ? k.Eier!.Trim() : AgentEmail;

        var mangler = new List<string>();
        if (orgnr.Length != 9) mangler.Add("organisasjonsnummer");
        if (string.IsNullOrWhiteSpace(ssn)) mangler.Add("signers " + FnrManglerGrunn(k.Foedselsnummer));
        if (string.IsNullOrWhiteSpace(mobil)) mangler.Add("mobilnummer");
        if ((k.OnsketLaanebelop ?? 0) <= 0) mangler.Add("ønsket lånebeløp");
        // Merk: bedriftslån (company loan) bruker IKKE DurationInMonths — løpetid kreves derfor ikke.
        if (string.IsNullOrWhiteSpace(agentEpost)) mangler.Add("agent-e-post (sakseier eller Instabank__AgentEmail)");
        if (mangler.Count > 0)
            return new(false, null, null, null, "Kan ikke sende bedriftslån — mangler: " + string.Join(", ", mangler));

        static object? Tekst(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        // Låneformål: bruk B2B-feltet (Investment/Liquidity/Other) hvis satt, ellers utled fra lånetype.
        var formaal = !string.IsNullOrWhiteSpace(k.BedriftLaaneformaal) ? k.BedriftLaaneformaal! : BedriftFormaal(k.Laaneformal);

        var application = new Dictionary<string, object?>
        {
            ["Product"] = new { Code = ProduktBedriftslaan },
            ["Calculation"] = new Dictionary<string, object?> { ["Amount"] = k.OnsketLaanebelop },
            ["Company"] = new Dictionary<string, object?>
            {
                ["OrganizationNumber"] = orgnr,
                ["EMail"] = k.Epost,
                ["MobilePhoneNumber"] = mobil,
            },
            ["Applicant"] = new Dictionary<string, object?>
            {
                ["SocialSecurityNumber"] = ssn,
                ["IsEmployedByCompany"] = k.BedriftAnsattISelskapet,
                ["HasOwnerShipInCompany"] = k.BedriftEierandelOver25,
                ["HasAnyOtherCompanyDebt"] = k.BedriftAnnenSelskapsgjeld,
            },
            ["Agent"] = new { Email = agentEpost },
            ["PurposeForLoan"] = new[] { formaal },
            ["AnyNewDebtLast12Months"] = k.BedriftNyGjeld12Mnd,
            ["CompanyProvideCollateral"] = k.BedriftStillerSikkerhet,
            ["IsPreOffer"] = preOffer,
            ["Reference"] = k.Id.ToString(),
        };
        // Valgfrie/utfyllende B2B-felt — utelat tomme.
        if (k.BedriftOmsetningIAar is > 0) application["EstimatedTurnOverThisYear"] = k.BedriftOmsetningIAar;
        if (k.BedriftOmsetningNesteAar is > 0) application["EstimatedTurnOverNextYear"] = k.BedriftOmsetningNesteAar;
        if (Tekst(k.BedriftBeskrivelse) is { } cd) application["CompanyDescription"] = cd;
        if (Tekst(k.BedriftMarkedsbeskrivelse) is { } md) application["MarketDescription"] = md;
        if (Tekst(k.BedriftLaaneformaalBeskrivelse) is { } pd) application["PurposeForLoanDescription"] = pd;
        if (Tekst(k.BedriftLaaneformaalAnnet) is { } pod) application["PurposeForLoanOtherDescription"] = pod;
        if (Tekst(k.BedriftKredittbruk) is { } cu) application["CreditUsage"] = cu;
        if (Tekst(k.BedriftMidlenesOpprinnelse) is { } oo) application["OriginOfFunds"] = new[] { oo };
        if (Tekst(k.BedriftMidlenesOpprinnelseAnnet) is { } ood) application["OriginOfFundsOtherDescription"] = ood;
        if (Tekst(k.BedriftSikkerhetBeskrivelse) is { } ccd) application["CompanyCollateralDescription"] = ccd;

        var r = await PostAsync("create", new { Application = application, DoSetAccepted = false });
        return r.Ok ? r : r with { Detalj = $"{r.Detalj} [sendt: beløp={k.OnsketLaanebelop:N0} kr, løpetid={(k.OnsketLopetidMnd?.ToString() ?? "ikke satt")} mnd]" };
    }

    // PurposeForLoan for bedrift: Investment | Liquidity | Other.
    private static string BedriftFormaal(string? formaal)
    {
        var x = (formaal ?? "").ToLowerInvariant();
        if (x.Contains("invest") || x.Contains("kjøp") || x.Contains("utstyr")) return "Investment";
        if (x.Contains("likvid") || x.Contains("drift") || x.Contains("refinansi")) return "Liquidity";
        return "Other";
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

    /// <summary>Diagnostisk årsak til at fnr «mangler» — skiller tomt, feil antall siffer,
    /// og «kunne ikke dekrypteres» (Gdpr__FieldKey mangler på prosessen som kjører sendekøen).</summary>
    private static string FnrManglerGrunn(string? fnr)
    {
        var raw = (fnr ?? "").Trim();
        if (raw.StartsWith("enc:1:", StringComparison.Ordinal))
            return "fødselsnummer (kryptering ikke aktiv i sendekøen — kunne ikke dekryptere; Gdpr__FieldKey mangler på denne instansen)";
        if (string.IsNullOrEmpty(raw)) return "fødselsnummer";
        var siffer = new string(raw.Where(char.IsDigit).ToArray()).Length;
        return $"gyldig fødselsnummer (fant {siffer} siffer på kortet, må være 11)";
    }

    // Modulus-11-validering av norsk fødselsnummer (håndterer også D-nummer, der
    // første siffer er +4). Sjekker begge kontrollsifrene — samme regler som bankene.
    private static bool ErGyldigFnr(string? fnr)
    {
        var s = new string((fnr ?? "").Where(char.IsDigit).ToArray());
        if (s.Length != 11) return false;
        var d = s.Select(c => c - '0').ToArray();
        int[] v1 = { 3, 7, 6, 1, 8, 9, 4, 5, 2 };
        int[] v2 = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };
        var sum1 = 0; for (var i = 0; i < 9; i++) sum1 += v1[i] * d[i];
        var k1 = 11 - (sum1 % 11); if (k1 == 11) k1 = 0;
        if (k1 == 10 || k1 != d[9]) return false;
        var sum2 = 0; for (var i = 0; i < 10; i++) sum2 += v2[i] * d[i];
        var k2 = 11 - (sum2 % 11); if (k2 == 11) k2 = 0;
        if (k2 == 10 || k2 != d[10]) return false;
        return true;
    }

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

    // Enkel e-post-sjekk: eierfeltet skal inneholde en «@» med tegn på begge sider.
    private static bool ErEpost(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1;
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
