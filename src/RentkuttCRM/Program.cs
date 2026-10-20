using System.Threading.RateLimiting;
using RentkuttCRM.Components;
using RentkuttCRM.Services;
using Supabase;

var builder = WebApplication.CreateBuilder(args);

// Logg-redaksjon: maskér fødselsnummer i ALL console-logg (stdout / Azure Log Stream) som
// et globalt sikkerhetsnett. Kun treff som består MOD11 + gyldig datoprefiks maskeres, så
// kontonummer o.l. rammes ikke. (Merk: telemetri som ev. innføres senere må ha egen redaksjon.)
builder.Logging.AddConsole(o => o.FormatterName = RentkuttCRM.Services.RedactingConsoleFormatter.FormatterNavn);
builder.Logging.AddConsoleFormatter<RentkuttCRM.Services.RedactingConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

// Supabase – url/key settes som App settings i Azure (Supabase__Url / Supabase__Key).
// Klienten registreres lazy (Scoped), så appen starter selv om nøklene ennå ikke er satt.
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];
builder.Services.AddScoped(_ => new Client(
    supabaseUrl ?? string.Empty,
    supabaseKey,
    new SupabaseOptions { AutoConnectRealtime = false }));

// Response-komprimering (Brotli/Gzip) — mindre payload, raskere lasting.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// Web API (controllers) + Swagger.
builder.Services.AddControllers().AddJsonOptions(o =>
    // Enum-verdier som strenger (f.eks. «FastAnsatt») i partner-API-et, ikke tall.
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // «partner»: kuratert, bank-rettet API-dokumentasjon (eksponeres også i produksjon).
    c.SwaggerDoc("partner", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Rentekutt Partner API",
        Version = "v1",
        Description =
            "Dette API-et lar en **bankpartner** ta imot lånesøknader fra Rentekutt og melde status tilbake.\n\n"
            + "## Slik fungerer det\n\n"
            + "1. **Logg inn** — `POST /api/partner/v1/auth/token` med `{ \"username\": \"…\", \"password\": \"…\" }` (utlevert av Rentekutt). Du får en `access_token` som varer 1 time.\n\n"
            + "2. **Se datamodellen** — `GET /api/partner/v1/soknad/eksempel` returnerer en ferdig utfylt eksempel-søknad du kan bygge mot.\n\n"
            + "3. **Hent én søknad på UUID** — spisset oppslag, ingen lister: `GET /api/partner/v1/boliglan/{referanse}` (boliglån) og "
            + "`GET /api/partner/v1/refinansiering/{referanse}` (refinansiering/forbrukslån). Referansen får du når Rentekutt varsler om en ny søknad. "
            + "Kun søknader sendt til DIN bank. "
            + "(Rentekutt kan også pushe søknader i samme format til et mottaks-endepunkt hos deg — avtales per integrasjon.)\n\n"
            + "4. **Meld status tilbake** — `POST /api/partner/v1/status` hver gang søknaden endrer status.\n\n"
            + "Send tokenet som header på alle kall: `Authorization: Bearer <access_token>`. "
            + "Trykk **Authorize** øverst til høyre for å lime inn tokenet og teste kallene direkte her i portalen.\n\n"
            + "## Fra Rentekutt til bank\n\n"
            + "- Svar `200 OK` eller `202 Accepted`.\n\n"
            + "- Timeout er 30 sekunder. Får vi ikke svar, prøver vi på nytt inntil 3 ganger med økende pause.\n\n"
            + "- Samme `referanse` (en UUID) kan derfor komme flere ganger — **oppdater den eksisterende søknaden, ikke opprett en ny** (idempotens).\n\n"
            + "## Fra bank til Rentekutt\n\n"
            + "Gyldige statusverdier: `Mottatt`, `UnderBehandling`, `TrengerDokumentasjon`, `Tilbud`, `Innvilget`, `Utbetalt`, `Avslatt`, `Kansellert`.\n\n"
            + "- Hver melding må ha en unik `hendelseId` (UUID) — samme hendelse registreres kun én gang.\n\n"
            + "- `referanse` må matche en kjent søknad; ukjent referanse gir `404`.\n\n"
            + "- Status `Tilbud` skal inneholde objektet `tilbud` (nominell + effektiv rente, etablerings- og termingebyr, månedsbeløp, totalkostnad, gyldig til).\n\n"
            + "- Status `Utbetalt` skal ha `utbetaltBelop` + `utbetaltDato`.",
    });
    // «v1»: full intern API-flate — kun i Development.
    if (builder.Environment.IsDevelopment())
        c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "Rentekutt (internt API)", Version = "v1" });

    // Partner-dokumentet inkluderer kun partner-gruppen; v1 (dev) tar med alt.
    c.DocInclusionPredicate((docName, api) => docName == "partner" ? api.GroupName == "partner" : true);

    // Korte skjemanavn (SoknadDto, ikke RentkuttCRM.Models.PartnerApi.SoknadDto).
    c.CustomSchemaIds(t => t.Name);

    // Rekkefølge på seksjonene: login+status øverst, så boliglån, så usikret gjeld.
    c.OrderActionsBy(d =>
    {
        var p = (d.RelativePath ?? "").ToLowerInvariant();
        var grp = (p.Contains("auth/token") || p.EndsWith("/status")) ? "0"
                : p.Contains("boliglan") ? "1" : "2";
        return grp + p;
    });

    // Bearer-token (gir «Authorize»-knapp i Swagger UI).
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Token fra /auth/token. Sendes som «Authorization: Bearer <token>».",
    });
    // Knytt Bearer-kravet globalt så «Authorize» faktisk sender tokenet (innloggingen ignorerer det).
    c.AddSecurityRequirement(doc => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", doc)] = new List<string>(),
    });

    // Feltbeskrivelser fra XML-dok.
    var xml = Path.Combine(AppContext.BaseDirectory, $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

// Rate limiting for offentlige API-endepunkter (mot enumerering/misbruk).
// «tredjepart» partisjoneres på token (ev. IP) — hindrer masseoppslag av lånestatus på mobilnr.
// «webhook» partisjoneres på IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("tredjepart", ctx =>
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : ctx.Request.Query["token"].ToString();
        var key = !string.IsNullOrWhiteSpace(token)
            ? "t:" + token
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });

    options.AddPolicy("webhook", ctx =>
    {
        var key = "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });

    // Partner-API: innlogging strengt per IP (brute-force-vern), øvrige kall per token.
    options.AddPolicy("partner-login", ctx =>
    {
        var key = "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
    options.AddPolicy("partner", ctx =>
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? "t:" + auth["Bearer ".Length..].Trim()
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
});

// Blazor (Interactive Server) – frontend i samme prosjekt.
// DetailedErrors KUN i Development (aldri lekke stack traces til klient i prod).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// Hev SignalR-meldingsgrensen for Blazor-circuiten fra standarden på 32 KB slik at
// bildeopplasting via <InputFile> (bug-vedlegg) ikke sprenger grensen. 10 MB gir god
// margin for skjermbilder; selve filstørrelsen valideres i tillegg i UI (maks 6 MB).
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o => o.MaximumReceiveMessageSize = 10 * 1024 * 1024);

// Innloggings-tilstand per økt (staging).
builder.Services.AddScoped<SessionState>();

// Innlogging + brukeradministrasjon (Supabase, med staging-fallback).
builder.Services.AddScoped<SupabaseUserService>();

// Feltnivåkryptering (fødselsnummer m.m.) — nøkkel via Gdpr__FieldKey.
builder.Services.AddSingleton<CryptoService>();

// Kundekort (lånesøknader).
builder.Services.AddScoped<KundekortService>();
builder.Services.AddScoped<OppfolgingService>();
builder.Services.AddScoped<GjeldsregisterService>();
builder.Services.AddScoped<LeadMottakService>();
builder.Services.AddScoped<BugService>();
builder.Services.AddScoped<ArbeidslisteService>();
builder.Services.AddScoped<RefinansieringMappingService>();
builder.Services.AddScoped<SakerUiState>();

// Tidsstemplede saksnotater.
builder.Services.AddScoped<NotatService>();

// Webhooks (inbound lead-mottak).
builder.Services.AddScoped<WebhookService>();

// Lagring av innkommende webhook-payloads (siste 50, for feilsøking).
builder.Services.AddScoped<WebhookPayloadService>();

// SMS (LinkMobility) + 2FA.
builder.Services.AddHttpClient("linkmobility");
builder.Services.AddSingleton<LinkMobilityService>();
builder.Services.AddSingleton<TwoFactorService>();

// Klaviyo (marketing/e-post-hendelser) — privat API-nøkkel kun fra server-config.
builder.Services.AddHttpClient("klaviyo");
builder.Services.AddSingleton<KlaviyoService>();

// Sporing av automatiske SMS-utsendinger (dedup + logg for Kommunikasjon-fanen).
builder.Services.AddScoped<SmsUtsendingService>();
builder.Services.AddScoped<SmsLoepService>();   // konfigurerbare SMS-løp (triggere + tidspunkt)

// Innstillinger (key/value).
builder.Services.AddScoped<SettingsService>();

// Fagforeninger (dropdown på kundekort, dynamisk utvidbar).
builder.Services.AddScoped<FagforeningService>();

// Nextcom webhook-levering (bank-autosending, f.eks. Soknedal Sparebank).
builder.Services.AddScoped<NextcomService>();

// Dialer (Zisson/Wave click-to-call).
builder.Services.AddScoped<ZissonService>();
builder.Services.AddScoped<DialerService>();
builder.Services.AddScoped<DialerCallService>();   // aktiv-anrop-tilstand for global ringebar
builder.Services.AddHostedService<ZissonCdrWorker>();   // fyller utfall/taletid på dialer-anrop fra Zisson CDR

// Hendelseslogg.
builder.Services.AddScoped<EventService>();

// Bankpartnere.
builder.Services.AddScoped<PartnerService>();

// Brønnøysund-oppslag (Enhetsregisteret, åpent API) — firma-status/fakta på B2B-kundekort.
builder.Services.AddHttpClient("brreg", c => c.BaseAddress = new Uri("https://data.brreg.no/"));
builder.Services.AddScoped<BrregService>();

// Bankregister (Finanstilsynets åpne virksomhetsregister) — fast liste for «Nåværende bank».
builder.Services.AddHttpClient("finanstilsynet", c => c.BaseAddress = new Uri("https://api.finanstilsynet.no/"));
builder.Services.AddSingleton<BankRegisterService>();
builder.Services.AddHostedService<BankRegisterWorker>();

// Produkter per bankpartner (provisjon per produkt, segment privat/bedrift).
builder.Services.AddScoped<PartnerProduktService>();

// Instabank Agent API (delegerte søknader for kunder som skal til Instabank).
builder.Services.AddScoped<InstabankService>();

// Logg over søknader sendt til bank (vises på kundekort + under Bank API).
builder.Services.AddScoped<BankSendingService>();

// Alarmer for stopp/feil (migrasjoner, banksending, sikkerhetsbryter) — vises og kvitteres i portalen.
builder.Services.AddScoped<AlarmService>();

// Endringslogg per kundekort (audit trail).
builder.Services.AddScoped<LoggService>();

// GDPR: anonymisering + sletting etter oppsatt antall måneder.
builder.Services.AddScoped<GdprService>();

// GDPR-kjøringslogg (én rad per jobbforsøk) — driver Alarm 1 (oppbevaringsjobb har ikke fullført).
builder.Services.AddScoped<GdprKjoringService>();

// Samtykke (GDPR) — egen entitet + gyldighetssjekk før banksending.
builder.Services.AddScoped<SamtykkeService>();

// Rutingsregler (logikk-matrisen) — driver «Forslag bank» i markedet.
builder.Services.AddScoped<RutingsregelService>();

// Merknadsregler — driver «Merknad»-badges i markedet (f.eks. «Boliglån UNG»).
builder.Services.AddScoped<MerknadsregelService>();

// Sikker sendekø: throttlet bakgrunnsarbeider som sender søknader til bank uten å bombardere API-et.
builder.Services.AddHostedService<BankSendWorker>();

// GDPR-jobb: daglig anonymisering/sletting etter oppsatt oppbevaringstid.
builder.Services.AddHostedService<GdprWorker>();

// GDPR-overvåking (hver time, prod): Alarm 1 (oppbevaringsjobb forsinket), Alarm 2 (fnr i klartekst)
// + reparasjonsjobb som krypterer klartekst og regenererer HMAC.
builder.Services.AddHostedService<GdprOvervaakWorker>();

// SMS-løp: 24-timers påminnelse til søknader som fortsatt står i «Påbegynt søknad».
builder.Services.AddHostedService<Paamindelse24tWorker>();

// Auto-send av uferdige søknader: leads som står i «Påbegynt søknad» 30 min etter registrering
// sendes automatisk til matchende banker med bryteren «Send uferdige etter 30 min» PÅ (kun prod).
builder.Services.AddHostedService<AutoPaabegyntWorker>();

// Auto-timeout: «Sendt bank» → «Sendt til bank - Timeout» etter innstilt antall dager.
builder.Services.AddHostedService<SendtBankTimeoutWorker>();

// Instabank-statussynk: spør hver 60. min på uavklarte, sendte søknader og oppdaterer status.
builder.Services.AddHostedService<InstabankStatusWorker>();

// SMS-maler + kundeutsending.
builder.Services.AddScoped<SmsMalService>();

// Transaksjonell Instabank-hendelse: SMS + Klaviyo/e-post når søknad innvilges i Instabank-API-et.
builder.Services.AddScoped<InstabankInnvilgetEventService>();

// Innkommende SMS (toveis) — kundesvar via LinkMobility MO-webhook, koblet til kundekort.
builder.Services.AddScoped<InnkommendeSmsService>();
builder.Services.AddSingleton<SmsInnDebugLogg>();

// Partner API — per-bank pålogging (hver bank ser kun sine egne søknader).
builder.Services.AddScoped<PartnerKredsService>();

// Beregningsmodell (finansieringsevne/likviditet).
builder.Services.AddScoped<BeregningService>();

// Katalog over kundedatafelt for universalfilteret i logikk-matrisen.
builder.Services.AddSingleton<CustomerFieldCatalog>();

// Postnummerregister — beriker søknader med kommune/poststed/fylke fra postnummer.
builder.Services.AddSingleton<PostnummerService>();

// Styringsrenten hentes live fra Norges Bank (cachet), med referanseliste som fallback.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<StyringsrenteService>();

// Databasemigrering (kjører SQL-filer i Migrations/ automatisk ved oppstart).
builder.Services.AddSingleton<DatabaseMigrator>();

var app = builder.Build();

// Kjør databasemigrasjoner ved oppstart (hopper over hvis ingen connection string).
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

// Opprett standard-admin hvis brukertabellen er tom (krever Supabase konfigurert).
using (var startupScope = app.Services.CreateScope())
{
    await startupScope.ServiceProvider.GetRequiredService<SupabaseUserService>().EnsureSeededPublicAsync();
    await startupScope.ServiceProvider.GetRequiredService<WebhookService>().EnsureSeededPublicAsync();
}

// HSTS + HTTPS-redirect i produksjon — tving http→https for domenet.
// (I Development hopper UseHttpsRedirection over når kun http-profilen kjører.)
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Partner-API-dokumentasjonen (/swagger) åpnes med én felles, symbolsk tilgangskode (ikke bankens
// brukernavn/passord — de brukes kun til selve API-innloggingen). Koden settes i Admin → Partner API.
// Nettleserens Basic-dialog godtar koden i enten brukernavn- eller passord-feltet.
// Ikke satt: åpent i Development, STENGT i produksjon (fail-closed).
static bool BasicKodeOk(HttpRequest req, string kode)
{
    var auth = req.Headers.Authorization.ToString();
    if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
    string dekodet;
    try { dekodet = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..].Trim())); }
    catch { return false; }
    var i = dekodet.IndexOf(':');
    var bruker = i < 0 ? dekodet : dekodet[..i];
    var passord = i < 0 ? "" : dekodet[(i + 1)..];
    return PartnerKredsService.KonstantLik(kode, bruker) || PartnerKredsService.KonstantLik(kode, passord);
}
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/swagger"), branch =>
    branch.Use(async (ctx, next) =>
    {
        // Alltid åpent lokalt (Development) — koden kreves kun i produksjon.
        if (app.Environment.IsDevelopment()) { await next(); return; }

        var kreds = ctx.RequestServices.GetRequiredService<PartnerKredsService>();
        var kode = await kreds.DocsKodeAsync();
        if (string.IsNullOrWhiteSpace(kode))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("Partner-API-dokumentasjonen er ikke aktivert.");
            return;
        }
        if (!BasicKodeOk(ctx.Request, kode!))
        {
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Rentekutt Partner API - oppgi tilgangskode\"";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    }));

// Swagger: i prod eksponeres KUN det kuraterte partner-dokumentet (på /swagger), bak Basic Auth.
// Den fulle interne API-flaten vises kun i Development.
app.UseSwagger();   // serverer /swagger/{dokument}/swagger.json for definerte dokumenter
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/partner/swagger.json", "Rentekutt Partner API");
    if (app.Environment.IsDevelopment())
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Rentekutt (internt API)");
    c.RoutePrefix = "swagger";       // portal.metapro.no/swagger
    c.DocumentTitle = "Rentekutt Partner API";
});

// Security-headere på ALLE svar (før static files/endepunkter).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"]        = "DENY";
    h["Referrer-Policy"]        = "no-referrer";
    h["Permissions-Policy"]     = "camera=(), microphone=(), geolocation=(), payment=()";
    // Swagger UI (partner-API-dok på /api-docs) har et inline init-skript → trenger en løsere
    // script-policy. Gjelder KUN dokumentasjonssidene; resten av appen beholder den strenge CSP-en.
    var erApiDok = ctx.Request.Path.StartsWithSegments("/api-docs") || ctx.Request.Path.StartsWithSegments("/swagger");
    // Streng CSP: appen har ingen inline-skript (blazor.web.js + app.css er eksterne).
    // Inline style-attributter brukes mange steder → 'unsafe-inline' kun for style.
    // SignalR-circuit trenger ws:/wss:.
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        (erApiDok ? "script-src 'self' 'unsafe-inline'; " : "script-src 'self'; ") +
        "connect-src 'self' ws: wss:;";
    await next();
});

app.UseResponseCompression();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAntiforgery();

app.UseAuthorization();

// API-endepunkter (under /api/...)
app.MapControllers();

// Blazor-frontend
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
