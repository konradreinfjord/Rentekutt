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

// Web API (controllers) + Swagger – beholdes som før.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Katalog over kundedatafelt for universalfilteret i logikk-matrisen.
builder.Services.AddSingleton<CustomerFieldCatalog>();

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

// HSTS i produksjon — be nettlesere alltid bruke HTTPS for domenet.
if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Swagger KUN i Development — eksponer ikke API-flaten i produksjon.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthorization();

// API-endepunkter (under /api/...)
app.MapControllers();

// Blazor-frontend
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
