using System.Reflection;
using Npgsql;

namespace RentkuttCRM.Services;

/// <summary>
/// Kjører SQL-migrasjoner mot Postgres (Supabase) automatisk ved oppstart.
///
/// Migrasjoner ligger som .sql-filer i mappen Migrations/ (embedded resources).
/// Hver fil kjøres én gang, i navnerekkefølge, og registreres i schema_migrations.
/// Push en ny migrasjonsfil → deploy → den kjøres automatisk. Ingen SQL Editor nødvendig.
///
/// Trenger en Postgres-connection string (ConnectionStrings:Postgres). Er den ikke
/// satt, hoppes migrering over (appen kjører videre i staging-modus).
/// </summary>
/// <summary>Diagnostikk fra siste migrator-kjøring — vises i Admin så vi ser om
/// migrasjonene faktisk når prod (uten å måtte lese Azure-logger).</summary>
public class MigrasjonStatus
{
    public bool Konfigurert { get; set; }
    public bool Kjort { get; set; }
    public int AnvendtTotalt { get; set; }
    public List<string> AnvendtNaa { get; } = new();
    public List<string> Utestaende { get; } = new();
    public List<string> Feilet { get; } = new();
    public bool SkjemaReloadOk { get; set; }
    public string? SisteFeil { get; set; }
    public DateTime? KjortAt { get; set; }
}

public class DatabaseMigrator
{
    private readonly string? _connectionString;
    private readonly ILogger<DatabaseMigrator> _log;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    /// <summary>Status fra siste kjøring (til Admin-diagnostikk).</summary>
    public MigrasjonStatus Status { get; private set; } = new();

    public DatabaseMigrator(IConfiguration cfg, ILogger<DatabaseMigrator> log)
    {
        _connectionString = PgConn.Normaliser(cfg.GetConnectionString("Postgres")
                            ?? cfg["ConnectionStrings:Postgres"]);
        _log = log;
    }

    /// <summary>Live-test av den direkte Postgres-tilkoblingen (ConnectionStrings:Postgres). Brukes i
    /// Admin for å vise om migrasjoner/GDPR-jobber har DB-kontakt, uavhengig av Supabase-API-et.</summary>
    public async Task<(bool ok, string detalj)> TestTilkoblingAsync()
    {
        if (!IsConfigured) return (false, "ConnectionStrings:Postgres er ikke satt.");
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("select 1", conn);
            await cmd.ExecuteScalarAsync();
            return (true, "Tilkoblet (direkte Postgres).");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Ber PostgREST laste skjema-cachen på nytt. Brukes selvhelbredende ved PGRST204/205
    /// (kolonne/tabell finnes i DB, men REST-laget kjenner den ikke ennå) og fra Admin manuelt.</summary>
    public async Task<bool> ReloadSchemaCacheAsync()
    {
        if (!IsConfigured) return false;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("notify pgrst, 'reload schema';", conn);
            await cmd.ExecuteNonQueryAsync();
            _log.LogInformation("Ba PostgREST laste skjema-cachen på nytt (reload)");
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Kunne ikke reloade PostgREST skjema-cache"); return false; }
    }

    public async Task MigrateAsync()
    {
        var status = new MigrasjonStatus { Konfigurert = IsConfigured, KjortAt = DateTime.UtcNow };
        Status = status;

        if (!IsConfigured)
        {
            _log.LogWarning("Ingen Postgres-connection string (ConnectionStrings:Postgres) — migrering hoppes over. Nye tabeller/kolonner når ikke prod automatisk.");
            status.SisteFeil = "ConnectionStrings:Postgres er ikke satt — migrering kjøres ikke.";
            return;
        }

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            // Kort timeout så oppstart ikke henger om DB er uroutbar (f.eks. IPv6).
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await conn.OpenAsync(cts.Token);

            await EnsureMigrationsTableAsync(conn);
            var applied = await GetAppliedAsync(conn);

            foreach (var (name, sql) in GetEmbeddedMigrations())
            {
                if (applied.Contains(name)) { status.AnvendtTotalt++; continue; }

                _log.LogInformation("Kjører migrasjon {Name}", name);
                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                        await cmd.ExecuteNonQueryAsync();

                    await using (var record = new NpgsqlCommand(
                        "insert into public.schema_migrations (filename) values (@f)", conn, tx))
                    {
                        record.Parameters.AddWithValue("f", name);
                        await record.ExecuteNonQueryAsync();
                    }

                    await tx.CommitAsync();
                    status.AnvendtNaa.Add(name);
                    status.AnvendtTotalt++;
                    _log.LogInformation("Migrasjon {Name} fullført", name);
                }
                catch (Exception mx)
                {
                    // VIKTIG: ikke stopp hele køen om én migrasjon feiler — logg, marker
                    // som utestående, og fortsett til neste. En enkelt feil skal ikke
                    // blokkere alle senere migrasjoner (det var den gamle bugen).
                    await tx.RollbackAsync();
                    status.Feilet.Add($"{name}: {mx.Message}");
                    status.SisteFeil = $"{name}: {mx.Message}";
                    _log.LogError(mx, "Migrasjon {Name} feilet — fortsetter til neste", name);
                }
            }

            // Utestående = migrasjoner som fortsatt ikke er registrert (feilet eller nye).
            var appliedEtter = await GetAppliedAsync(conn);
            foreach (var (name, _) in GetEmbeddedMigrations())
                if (!appliedEtter.Contains(name)) status.Utestaende.Add(name);

            // Be PostgREST laste skjema-cachen på nytt. Tabeller/kolonner lagt til via
            // denne direkte DB-tilkoblingen er ellers ikke synlige for REST-laget som
            // Supabase-klienten bruker (PGRST205/PGRST204), og insert feiler stille.
            // Kjøres hver oppstart (idempotent) — selv om noen migrasjoner feilet.
            try
            {
                await using var reload = new NpgsqlCommand("notify pgrst, 'reload schema';", conn);
                await reload.ExecuteNonQueryAsync();
                status.SkjemaReloadOk = true;
                _log.LogInformation("Ba PostgREST laste skjema-cachen på nytt");
            }
            catch (Exception ex) { _log.LogWarning(ex, "Klarte ikke be PostgREST laste skjema på nytt"); }

            status.Kjort = true;
        }
        catch (Exception ex)
        {
            // Ikke krasj appen — logg tydelig. Innlogging vil da feile til DB er på plass.
            status.SisteFeil = ex.Message;
            _log.LogError(ex, "Databasemigrering feilet");
        }
    }

    private static async Task EnsureMigrationsTableAsync(NpgsqlConnection conn)
    {
        const string sql = @"
            create table if not exists public.schema_migrations (
                filename   text primary key,
                applied_at timestamptz not null default now()
            );";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> GetAppliedAsync(NpgsqlConnection conn)
    {
        var set = new HashSet<string>();
        await using var cmd = new NpgsqlCommand("select filename from public.schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            set.Add(reader.GetString(0));
        return set;
    }

    private static IEnumerable<(string Name, string Sql)> GetEmbeddedMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        const string marker = ".Migrations.";
        var names = asm.GetManifestResourceNames()
            .Where(n => n.Contains(marker) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in names)
        {
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var sr = new StreamReader(stream);
            var sql = sr.ReadToEnd();
            // kort, lesbart navn (filnavnet), f.eks. "0001_app_users.sql"
            var shortName = resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            yield return (shortName, sql);
        }
    }
}
