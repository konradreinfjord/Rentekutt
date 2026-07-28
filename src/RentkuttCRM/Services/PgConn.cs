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
            // Manuell parsing (ikke new Uri): et DB-passord kan inneholde tegn som «@ : / ?» som
            // knekker URI-parseren. Vi deler derfor robust selv — passord med spesialtegn tolereres.
            var uten = t[(t.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var sporsmal = uten.IndexOf('?');                       // strip ev. query (?sslmode=…)
            if (sporsmal >= 0) uten = uten[..sporsmal];

            // userinfo @ vert:port/db — del på SISTE '@' (passordet kan inneholde '@').
            var at = uten.LastIndexOf('@');
            var userinfo = at >= 0 ? uten[..at] : "";
            var vertDb = at >= 0 ? uten[(at + 1)..] : uten;

            // userinfo = bruker:passord — del på FØRSTE ':' (passordet kan inneholde ':').
            var user = userinfo; var pass = "";
            var kolon = userinfo.IndexOf(':');
            if (kolon >= 0) { user = userinfo[..kolon]; pass = userinfo[(kolon + 1)..]; }

            // vert:port/db
            var hostPort = vertDb; var db = "postgres";
            var slash = vertDb.IndexOf('/');
            if (slash >= 0) { hostPort = vertDb[..slash]; db = vertDb[(slash + 1)..]; }
            var host = hostPort; var port = 5432;
            var hk = hostPort.LastIndexOf(':');
            if (hk >= 0 && int.TryParse(hostPort[(hk + 1)..], out var p)) { host = hostPort[..hk]; port = p; }

            static string Avkod(string s) => s.Contains('%') ? Uri.UnescapeDataString(s) : s;
            var sb = new NpgsqlConnectionStringBuilder
            {
                Host = Avkod(host),
                Port = port > 0 ? port : 5432,
                Database = string.IsNullOrEmpty(db) ? "postgres" : Avkod(db),
                Username = Avkod(user),
                Password = Avkod(pass),
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
