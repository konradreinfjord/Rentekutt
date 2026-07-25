using Npgsql;

namespace RentkuttCRM.Services;

/// <summary>
/// GDPR-etterlevelse: anonymiserer og sletter kundekort etter oppsatt antall måneder
/// (innstillingene gdpr_anonymize_months / gdpr_delete_months, regnet fra created_at).
///
/// Kjører daglig via <see cref="GdprWorker"/>, og kan trigges manuelt fra Admin → GDPR.
/// Bruker direkte Postgres-tilkobling (ConnectionStrings:Postgres) for pålitelig
/// bulk-operasjon — samme tilkobling som databasemigrasjonene.
/// </summary>
public class GdprService
{
    private readonly string? _conn;
    private readonly CryptoService _krypto;
    private readonly ILogger<GdprService> _log;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_conn);

    public GdprService(IConfiguration cfg, CryptoService krypto, ILogger<GdprService> log)
    {
        _conn = cfg.GetConnectionString("Postgres") ?? cfg["ConnectionStrings:Postgres"];
        _krypto = krypto;
        _log = log;
    }

    /// <summary>Kjør anonymisering + sletting. Med <paramref name="torrkjor"/>=true telles
    /// kun hva som VILLE blitt behandlet, uten å endre data (forhåndsvisning).
    /// Returnerer antall behandlet og evt. feil.</summary>
    public async Task<(int anonymisert, int slettet, string? feil)> KjorAsync(int anonMnd, int slettMnd, bool torrkjor = false, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (0, 0, "ConnectionStrings:Postgres er ikke satt — GDPR-jobben kan ikke kjøre.");
        if (anonMnd < 1) anonMnd = 1;
        if (slettMnd < 1) slettMnd = 1;

        var anonGrense = DateTime.UtcNow.AddMonths(-anonMnd);
        var slettGrense = DateTime.UtcNow.AddMonths(-slettMnd);

        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var lenket = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token))
                await conn.OpenAsync(lenket.Token);

            if (torrkjor)
            {
                int aForhaands, sForhaands;
                await using (var cmd = new NpgsqlCommand("select count(*) from public.kundekort where created_at < @g and anonymisert_at is null;", conn))
                { cmd.Parameters.AddWithValue("g", anonGrense); aForhaands = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
                await using (var cmd = new NpgsqlCommand("select count(*) from public.kundekort where created_at < @g;", conn))
                { cmd.Parameters.AddWithValue("g", slettGrense); sForhaands = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
                return (aForhaands, sForhaands, null);
            }

            // 1) Anonymiser kundekort eldre enn anon-grensen (ikke allerede anonymisert):
            //    null ut identitet/kontakt/medsøker/notater, scrub kunde_id (ofte = fnr),
            //    behold finansielle/statistiske felt + geografi + datoer + kilde.
            const string anonSql = @"
update public.kundekort set
    fullt_navn = null, foedselsnummer = null, mobilnummer = null, epost = null,
    adresse = null, postnummer = null, poststed = null, kontonummer = null,
    medsoker_navn = null, medsoker_foedselsnummer = null, medsoker_mobil = null,
    medsoker_epost = null, medsoker_adresse = null, medsoker_postnummer = null, medsoker_poststed = null,
    notater = null, kunde_id = 'anonymisert', anonymisert_at = now()
where created_at < @grense and anonymisert_at is null;";
            int anon;
            await using (var cmd = new NpgsqlCommand(anonSql, conn))
            {
                cmd.Parameters.AddWithValue("grense", anonGrense);
                anon = await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2) Fjern PII i relaterte tabeller for anonymiserte kort (notat/logg/banksending-navn).
            await ExecAsync(conn, "delete from public.saksnotat where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);
            await ExecAsync(conn, "delete from public.kundekort_logg where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);
            await ExecAsync(conn, "update public.banksending set kunde_navn = null where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);

            // 3) Slett kundekort eldre enn slette-grensen (kaskade rydder banksending/saksnotat/kundekort_logg).
            int slett;
            await using (var cmd = new NpgsqlCommand("delete from public.kundekort where created_at < @grense;", conn))
            {
                cmd.Parameters.AddWithValue("grense", slettGrense);
                slett = await cmd.ExecuteNonQueryAsync(ct);
            }
            // Rydd gamle alarmer (detalj kan inneholde kundenavn).
            await using (var cmd = new NpgsqlCommand("delete from public.alarm where created_at < @grense;", conn))
            {
                cmd.Parameters.AddWithValue("grense", slettGrense);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 4) Logg kjøringen (systemhendelse, ingen PII).
            await ExecAsync(conn, $"insert into public.hendelser (type, beskrivelse, kilde) values ('GDPR', 'Anonymiserte {anon}, slettet {slett} kundekort', 'GDPR-jobb');", ct);

            _log.LogInformation("GDPR-jobb: anonymiserte {Anon}, slettet {Slett}", anon, slett);
            return (anon, slett, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GDPR-jobb feilet");
            return (0, 0, ex.Message);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ================= Registrertes rettigheter (innsyn/sletting per person) =================

    public record PersonTreff(Guid Id, string? Navn, string? Fnr, string? Mobil, string? Epost, string? Status, DateTime OpprettetAt, bool Anonymisert);

    private static string RensSok(string sok) => new string((sok ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    // Matcher på søkbar HMAC (kryptert fnr) OG rå verdi (klartekst, for rader som ennå
    // ikke er bakfylt). Mobil/e-post er ikke kryptert og matches direkte.
    private const string SokFilter =
        "(fnr_hmac = @h or foedselsnummer = @s or kunde_id = @s or mobilnummer = @s or lower(epost) = lower(@s))";

    private void LeggTilSokParams(NpgsqlCommand cmd, string sok)
    {
        cmd.Parameters.AddWithValue("s", sok);
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)_krypto.HmacFnr(sok) ?? DBNull.Value,
        });
    }

    /// <summary>Søk opp alle kundekort for én person (på fnr, mobil, kunde-id eller e-post).</summary>
    public async Task<(List<PersonTreff> treff, string? feil)> SokPersonAsync(string sok)
    {
        if (!IsConfigured) return (new(), "ConnectionStrings:Postgres er ikke satt.");
        sok = RensSok(sok);
        if (sok.Length < 3) return (new(), "Skriv inn fødselsnummer, mobilnummer eller e-post.");
        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            var liste = new List<PersonTreff>();
            await using var cmd = new NpgsqlCommand(
                "select id, fullt_navn, foedselsnummer, mobilnummer, epost, status, created_at, anonymisert_at " +
                "from public.kundekort where " + SokFilter + " order by created_at desc;", conn);
            LeggTilSokParams(cmd, sok);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                liste.Add(new PersonTreff(
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : _krypto.Avdekk(r.GetString(2)),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetDateTime(6),
                    !r.IsDBNull(7)));
            }
            return (liste, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Personsøk (GDPR) feilet"); return (new(), ex.Message); }
    }

    /// <summary>Full eksport (JSON) av alle personopplysninger vi har om personen — for innsynskrav.</summary>
    public async Task<(string? json, int antallKort, string? feil)> EksporterPersonAsync(string sok)
    {
        if (!IsConfigured) return (null, 0, "ConnectionStrings:Postgres er ikke satt.");
        sok = RensSok(sok);
        if (sok.Length < 3) return (null, 0, "Ugyldig søk.");
        const string sql = @"
select coalesce(json_agg(json_build_object(
    'kundekort', row_to_json(k),
    'saksnotat',    (select coalesce(json_agg(row_to_json(sn)),'[]') from public.saksnotat sn      where sn.kundekort_id = k.id),
    'endringslogg', (select coalesce(json_agg(row_to_json(lg)),'[]') from public.kundekort_logg lg where lg.kundekort_id = k.id),
    'banksending',  (select coalesce(json_agg(row_to_json(bs)),'[]') from public.banksending bs    where bs.kundekort_id = k.id),
    'samtykke',     (select coalesce(json_agg(row_to_json(sm)),'[]') from public.samtykke sm       where sm.kundekort_id = k.id)
) order by k.created_at desc), '[]')::text
from public.kundekort k where " + SokFilter + ";";
        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            LeggTilSokParams(cmd, sok);
            var json = (await cmd.ExecuteScalarAsync()) as string ?? "[]";
            var (dekryptert, antall) = DekrypterEksport(json);
            return (dekryptert, antall, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Innsynseksport (GDPR) feilet"); return (null, 0, ex.Message); }
    }

    /// <summary>Slett ALT vi har om personen (rett til sletting). Fjerner alle kundekort + relaterte rader.</summary>
    public async Task<(int slettet, string? feil)> SlettPersonAsync(string sok, string? aktor)
    {
        if (!IsConfigured) return (0, "ConnectionStrings:Postgres er ikke satt.");
        sok = RensSok(sok);
        if (sok.Length < 3) return (0, "Ugyldig søk.");
        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // Finn berørte kundekort-id-er.
            var ids = new List<Guid>();
            await using (var cmd = new NpgsqlCommand("select id from public.kundekort where " + SokFilter + ";", conn, (NpgsqlTransaction)tx))
            {
                LeggTilSokParams(cmd, sok);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
            }
            if (ids.Count == 0) { await tx.RollbackAsync(); return (0, null); }

            // Slett barn eksplisitt (uavhengig av kaskade-oppsett) + selve kundekortet.
            foreach (var tabell in new[] { "saksnotat", "kundekort_logg", "banksending", "samtykke" })
                await using (var cmd = new NpgsqlCommand($"delete from public.{tabell} where kundekort_id = any(@ids);", conn, (NpgsqlTransaction)tx))
                { cmd.Parameters.AddWithValue("ids", ids.ToArray()); await cmd.ExecuteNonQueryAsync(); }

            int slett;
            await using (var cmd = new NpgsqlCommand("delete from public.kundekort where id = any(@ids);", conn, (NpgsqlTransaction)tx))
            { cmd.Parameters.AddWithValue("ids", ids.ToArray()); slett = await cmd.ExecuteNonQueryAsync(); }

            // Loggfør hendelsen uten PII (kun antall + hvem som utførte).
            await using (var cmd = new NpgsqlCommand(
                "insert into public.hendelser (type, beskrivelse, kilde) values ('GDPR', @b, @kilde);", conn, (NpgsqlTransaction)tx))
            {
                cmd.Parameters.AddWithValue("b", $"Slettet {slett} kundekort etter sletteforespørsel (registrertes rettigheter)");
                cmd.Parameters.AddWithValue("kilde", string.IsNullOrWhiteSpace(aktor) ? "Innsyn & sletting" : aktor);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _log.LogInformation("GDPR-sletting: {N} kundekort slettet av {Aktor}", slett, aktor ?? "?");
            return (slett, null);
        }
        catch (Exception ex) { _log.LogError(ex, "GDPR-sletting per person feilet"); return (0, ex.Message); }
    }

    // Innsyn skal gi den registrerte lesbare data — dekrypter fnr-feltene i eksporten.
    private (string json, int antall) DekrypterEksport(string json)
    {
        try
        {
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsArray();
            if (arr is null) return (json, 0);
            foreach (var el in arr)
            {
                if (el?["kundekort"] is not System.Text.Json.Nodes.JsonObject kk) continue;
                foreach (var felt in new[] { "foedselsnummer", "medsoker_foedselsnummer", "kunde_id" })
                {
                    var v = kk[felt]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(v)) kk[felt] = _krypto.Avdekk(v);
                }
            }
            return (arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), arr.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Dekryptering av eksport feilet — leverer rå JSON"); return (json, 0); }
    }
}
