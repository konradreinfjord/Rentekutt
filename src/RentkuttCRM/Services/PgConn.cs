using Npgsql;

namespace RentkuttCRM.Services;

/// <summary>
/// Normaliserer en Postgres-tilkoblingsstreng. Supabase oppgir gjerne URI-formatet
/// (<c>postgresql://bruker:passord@host:5432/db</c>), mens Npgsql krever nøkkelord-formatet
/// (<c>Host=…;Username=…;Password=…</c>). Feil format gir «Format of the initialization string
/// does not conform to specification». Denne konverterer URI → nøkkelord automatisk, så begge
/// varianter fungerer i Azure-innstillingen <c>ConnectionStrings__Postgres</c>.
/// </summary>
public static class PgConn
{
    public static string? Normaliser(string? conn)
    {
        if (string.IsNullOrWhiteSpace(conn)) return conn;
        var t = conn.Trim();
        if (!t.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return conn; // allerede nøkkelord-format

        try
        {
            var uri = new Uri(t);
            var deler = uri.UserInfo.Split(':', 2);
            var db = uri.AbsolutePath.Trim('/');
            var sb = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = string.IsNullOrEmpty(db) ? "postgres" : Uri.UnescapeDataString(db),
                Username = Uri.UnescapeDataString(deler[0]),
                Password = deler.Length > 1 ? Uri.UnescapeDataString(deler[1]) : "",
                SslMode = SslMode.Require,
            };
            return sb.ConnectionString;
        }
        catch
        {
            return conn; // la Npgsql gi sin egen feil hvis URI-en er ugyldig
        }
    }
}
