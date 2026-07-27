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
    notater = null, kunde_id = 'anonymisert', fnr_hmac = null, anonymisert_at = now()
where created_at < @grense and anonymisert_at is null;";
            int anon;
            await using (var cmd = new NpgsqlCommand(anonSql, conn))
            {
                cmd.Parameters.AddWithValue("grense", anonGrense);
                anon = await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2) Fjern PII i relaterte tabeller for anonymiserte kort (notat/logg/banksending-navn).
            //    Revisjonssporet er append-only — autoriser den kontrollerte oppryddingen for denne økten.
            await ExecAsync(conn, "set app.allow_log_purge = 'on';", ct);
            await ExecAsync(conn, "delete from public.saksnotat where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);
            await ExecAsync(conn, "delete from public.kundekort_logg where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);
            await ExecAsync(conn, "reset app.allow_log_purge;", ct);
            await ExecAsync(conn, "update public.banksending set kunde_navn = null where kundekort_id in (select id from public.kundekort where anonymisert_at is not null);", ct);
            // Alarmer kan inneholde kundenavn i detalj (matchet via noekkel som inneholder kundekort-id).
            await ExecAsync(conn, "delete from public.alarm a using public.kundekort k where k.anonymisert_at is not null and a.noekkel like '%' || k.id::text || '%';", ct);

            // 3) Slett kundekort eldre enn slette-grensen (kaskade rydder banksending/saksnotat/kundekort_logg).
            int slett;
            await using (var cmd = new NpgsqlCommand("delete from public.kundekort where created_at < @grense;", conn))
            {
                cmd.Parameters.AddWithValue("grense", slettGrense);
                slett = await cmd.ExecuteNonQueryAsync(ct);
            }
            // Rydd gamle alarmer — men KUN de som er kvittert ut manuelt. Ukvitterte alarmer
            // (f.eks. «kryptering AV» / klartekst-skriving) bevares til en person har sett og
            // kvittert dem, slik at hendelsessporet ikke forsvinner av seg selv. PII-bærende
            // banksending-alarmer ryddes uansett via kundekort-livssyklusen (anonymisering/sletting).
            await using (var cmd = new NpgsqlCommand("delete from public.alarm where created_at < @grense and kvittert = true;", conn))
            {
                cmd.Parameters.AddWithValue("grense", slettGrense);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Rydd rå payloads knyttet til kort som nå er slettet eller anonymisert (PII i payloaden).
            await ExecAsync(conn,
                "delete from public.webhook_payload wp where wp.kundekort_id is not null and " +
                "(not exists (select 1 from public.kundekort k where k.id = wp.kundekort_id) " +
                " or wp.kundekort_id in (select id from public.kundekort where anonymisert_at is not null));", ct);

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

    /// <summary>Live krypteringsdiagnose lest direkte fra databasen (ikke fra lagret innstilling):
    /// antall rader med fødselsnummer i klartekst, og antall med fnr satt men manglende HMAC.</summary>
    public async Task<(int uKryptert, int manglerHmac, string? feil)> KrypteringsDiagnoseAsync()
    {
        if (!IsConfigured) return (0, 0, "ConnectionStrings:Postgres er ikke satt.");
        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            int uKryptert, manglerHmac;
            await using (var cmd = new NpgsqlCommand(
                "select count(*) from public.kundekort where foedselsnummer is not null and foedselsnummer not like 'enc:1:%';", conn))
                uKryptert = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            await using (var cmd = new NpgsqlCommand(
                "select count(*) from public.kundekort where foedselsnummer is not null and fnr_hmac is null;", conn))
                manglerHmac = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return (uKryptert, manglerHmac, null);
        }
        catch (Exception ex) { _log.LogError(ex, "Krypteringsdiagnose feilet"); return (0, 0, ex.Message); }
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
    public async Task<(string? json, int antallKort, string? feil)> EksporterPersonAsync(string sok, string? aktor = null)
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
    'samtykke',     (select coalesce(json_agg(row_to_json(sm)),'[]') from public.samtykke sm       where sm.kundekort_id = k.id),
    'alarm',        (select coalesce(json_agg(row_to_json(al)),'[]') from public.alarm al          where al.noekkel like '%' || k.id::text || '%')
) order by k.created_at desc), '[]')::text
from public.kundekort k where " + SokFilter + ";";
        try
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            LeggTilSokParams(cmd, sok);
            var json = (await cmd.ExecuteScalarAsync()) as string ?? "[]";
            var (dekryptert, ids) = DekrypterEksport(json);

            // Loggfør innsynet i revisjonssporet (append-only INSERT — ikke blokkert av triggeren).
            if (ids.Count > 0)
            {
                await using var logg = new NpgsqlCommand(
                    "insert into public.kundekort_logg (kundekort_id, aktor, tekst, kategori, begrunnelse) " +
                    "select x, @aktor, 'Innsynseksport av persondata', 'innsyn', 'Registrertes innsynsrett (art. 15)' " +
                    "from unnest(@ids) as x;", conn);
                logg.Parameters.AddWithValue("aktor", (object?)aktor ?? "Innsyn & sletting");
                logg.Parameters.AddWithValue("ids", ids.ToArray());
                await logg.ExecuteNonQueryAsync();
            }
            return (dekryptert, ids.Count, null);
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

            // Revisjonssporet er append-only — autoriser den kontrollerte sletteveien for denne transaksjonen.
            await using (var cmd = new NpgsqlCommand("set local app.allow_log_purge = 'on';", conn, (NpgsqlTransaction)tx))
                await cmd.ExecuteNonQueryAsync();

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
            // webhook_payload inkluderes: rå payload for saken (viser sist mottatt) skal også bort.
            foreach (var tabell in new[] { "saksnotat", "kundekort_logg", "banksending", "samtykke", "webhook_payload" })
                await using (var cmd = new NpgsqlCommand($"delete from public.{tabell} where kundekort_id = any(@ids);", conn, (NpgsqlTransaction)tx))
                { cmd.Parameters.AddWithValue("ids", ids.ToArray()); await cmd.ExecuteNonQueryAsync(); }

            // Alarmer refererer kortet via noekkel (som inneholder kundekort-id) og kan ha kundenavn i detalj.
            foreach (var kid in ids)
                await using (var cmd = new NpgsqlCommand("delete from public.alarm where noekkel like @m;", conn, (NpgsqlTransaction)tx))
                { cmd.Parameters.AddWithValue("m", "%" + kid + "%"); await cmd.ExecuteNonQueryAsync(); }

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
    // Returnerer pen JSON + id-ene som ble eksportert (til innsyns-logging).
    private (string json, List<Guid> ids) DekrypterEksport(string json)
    {
        var ids = new List<Guid>();
        try
        {
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsArray();
            if (arr is null) return (json, ids);
            foreach (var el in arr)
            {
                if (el?["kundekort"] is not System.Text.Json.Nodes.JsonObject kk) continue;
                if (Guid.TryParse(kk["id"]?.GetValue<string>(), out var kid)) ids.Add(kid);
                foreach (var felt in new[] { "foedselsnummer", "medsoker_foedselsnummer", "kunde_id" })
                {
                    var v = kk[felt]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(v)) kk[felt] = _krypto.Avdekk(v);
                }
            }
            return (arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ids);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Dekryptering av eksport feilet — leverer rå JSON"); return (json, ids); }
    }
}
