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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger eksponeres også i produksjon → https://rentkutt-crm.azurewebsites.net/swagger
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();
app.MapControllers();

app.Run();
