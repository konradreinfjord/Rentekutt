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
public class DatabaseMigrator
{
    private readonly string? _connectionString;
    private readonly ILogger<DatabaseMigrator> _log;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public DatabaseMigrator(IConfiguration cfg, ILogger<DatabaseMigrator> log)
    {
        _connectionString = cfg.GetConnectionString("Postgres")
                            ?? cfg["ConnectionStrings:Postgres"];
        _log = log;
    }

    public async Task MigrateAsync()
    {
        if (!IsConfigured)
        {
            _log.LogInformation("Ingen Postgres-connection string — hopper over migrering (staging-modus).");
            return;
        }

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            // Kort timeout så oppstart ikke henger om DB er uroutbar (f.eks. IPv6).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await conn.OpenAsync(cts.Token);

            await EnsureMigrationsTableAsync(conn);
            var applied = await GetAppliedAsync(conn);

            foreach (var (name, sql) in GetEmbeddedMigrations())
            {
                if (applied.Contains(name)) continue;

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
                    _log.LogInformation("Migrasjon {Name} fullført", name);
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            // Ikke krasj appen — logg tydelig. Innlogging vil da feile til DB er på plass.
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
