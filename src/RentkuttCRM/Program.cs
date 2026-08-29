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

// Web API (controllers) + Swagger – beholdes som før.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Innstillinger (key/value).
builder.Services.AddScoped<SettingsService>();

// Fagforeninger (dropdown på kundekort, dynamisk utvidbar).
builder.Services.AddScoped<FagforeningService>();

// Dialer (Zisson/Wave click-to-call).
builder.Services.AddScoped<ZissonService>();
builder.Services.AddScoped<DialerService>();
builder.Services.AddScoped<DialerCallService>();   // aktiv-anrop-tilstand for global ringebar

// Hendelseslogg.
builder.Services.AddScoped<EventService>();

// Bankpartnere.
builder.Services.AddScoped<PartnerService>();

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

// Auto-timeout: «Sendt bank» → «Sendt til bank - Timeout» etter innstilt antall dager.
builder.Services.AddHostedService<SendtBankTimeoutWorker>();

// Instabank-statussynk: spør hver 60. min på uavklarte, sendte søknader og oppdaterer status.
builder.Services.AddHostedService<InstabankStatusWorker>();

// SMS-maler + kundeutsending.
builder.Services.AddScoped<SmsMalService>();

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

// Swagger KUN i Development — eksponer ikke API-flaten i produksjon.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Security-headere på ALLE svar (før static files/endepunkter).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"]        = "DENY";
    h["Referrer-Policy"]        = "no-referrer";
    h["Permissions-Policy"]     = "camera=(), microphone=(), geolocation=(), payment=()";
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
        "script-src 'self'; " +
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
