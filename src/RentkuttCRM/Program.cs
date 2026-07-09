using System.Threading.RateLimiting;
using RentkuttCRM.Components;
using RentkuttCRM.Services;
using Supabase;

var builder = WebApplication.CreateBuilder(args);

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

// Innloggings-tilstand per økt (staging).
builder.Services.AddScoped<SessionState>();

// Innlogging + brukeradministrasjon (Supabase, med staging-fallback).
builder.Services.AddScoped<SupabaseUserService>();

// Kundekort (lånesøknader).
builder.Services.AddScoped<KundekortService>();

// Tidsstemplede saksnotater.
builder.Services.AddScoped<NotatService>();

// Webhooks (inbound lead-mottak).
builder.Services.AddScoped<WebhookService>();

// SMS (LinkMobility) + 2FA.
builder.Services.AddHttpClient("linkmobility");
builder.Services.AddSingleton<LinkMobilityService>();
builder.Services.AddSingleton<TwoFactorService>();

// Innstillinger (key/value).
builder.Services.AddScoped<SettingsService>();

// Hendelseslogg.
builder.Services.AddScoped<EventService>();

// Bankpartnere.
builder.Services.AddScoped<PartnerService>();

// Instabank Agent API (delegerte søknader for kunder som skal til Instabank).
builder.Services.AddScoped<InstabankService>();

// SMS-maler + kundeutsending.
builder.Services.AddScoped<SmsMalService>();

// Beregningsmodell (finansieringsevne/likviditet).
builder.Services.AddScoped<BeregningService>();

// Katalog over kundedatafelt for universalfilteret i logikk-matrisen.
builder.Services.AddSingleton<CustomerFieldCatalog>();

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
