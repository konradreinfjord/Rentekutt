using System.Globalization;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Sender leads til en bank som mottar via Nextcom «landing page»-webhook (form-POST).
/// Feltnavnene er Nextcom-standard (FirstName/SecondName/Email/CellPhone/Extra2-4/Comments), så
/// én tjeneste dekker alle Nextcom-banker — URL-en (med friendlyUrl) er per bank.
/// </summary>
public class NextcomService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<NextcomService> _log;

    public NextcomService(IHttpClientFactory http, ILogger<NextcomService> log)
    {
        _http = http;
        _log = log;
    }

    public record Resultat(bool Ok, string? EksternRef, string Detalj);

    /// <summary>POSTer kundens kontakt- og lånedata til Nextcom-landingssiden (form-urlencoded).
    /// Suksess = 2xx; contactId/orderId returneres som ekstern-referanse.</summary>
    public async Task<Resultat> SendAsync(string? url, Kundekort k, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return new(false, null, "Mangler webhook-URL for banken.");

        var (fornavn, etternavn) = DelNavn(k.FulltNavn);
        // Nextcom-mapping (med maks-lengder fra skjemaet; verdier avkortes).
        var form = new Dictionary<string, string>
        {
            ["FirstName"] = Kutt(fornavn, 100),
            ["SecondName"] = Kutt(etternavn, 100),
            ["Email"] = Kutt(k.Epost, 100),
            ["CellPhone"] = Kutt(Sifre(k.Mobilnummer), 15),
            ["Extra2"] = Kutt(k.NavarendeBank, 15),                                  // Nåværende bank
            ["Extra3"] = Kutt(k.NaavaerendeRente?.ToString("0.##", Inv), 15),        // Nåværende rente
            ["Extra4"] = Kutt(k.OnsketLaanebelop?.ToString("0", Inv), 15),           // Lånesum
            ["Comments"] = "",
        };

        try
        {
            var c = _http.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(25);
            using var content = new FormUrlEncodedContent(form);
            using var res = await c.PostAsync(url, content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                var reff = LesRef(body);
                return new(true, reff, reff is null ? "Sendt til bank (Nextcom)." : $"Sendt til bank (Nextcom) — ref {reff}.");
            }
            _log.LogWarning("Nextcom-sending feilet {Status}: {Body}", (int)res.StatusCode, Kort(body));
            return new(false, null, $"Nextcom svarte HTTP {(int)res.StatusCode}: {Kort(body)}");
        }
        catch (TaskCanceledException) { return new(false, null, "Tidsavbrudd mot Nextcom (svarte ikke innen 25 s)."); }
        catch (Exception ex) { _log.LogWarning(ex, "Nextcom-sending kastet unntak"); return new(false, null, "Teknisk feil ved sending: " + ex.Message); }
    }

    // Plukker contactId/orderId fra JSON-svaret ({"contactId":.., "orderId":..}).
    private static string? LesRef(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var cid = r.TryGetProperty("contactId", out var ci) ? ci.ToString() : null;
            var oid = r.TryGetProperty("orderId", out var oi) ? oi.ToString() : null;
            var deler = new[] { cid, oid }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            return deler.Length == 0 ? null : string.Join("/", deler);
        }
        catch { return null; }
    }

    private static (string? fornavn, string? etternavn) DelNavn(string? navn)
    {
        if (string.IsNullOrWhiteSpace(navn)) return (null, null);
        var d = navn.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return d.Length == 1 ? (d[0], null) : (d[0], d[1]);
    }

    private static string Sifre(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
    private static string Kutt(string? s, int maks) => string.IsNullOrEmpty(s) ? "" : (s.Length > maks ? s[..maks] : s);
    private static string Kort(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 250 ? s[..250] + "…" : s);
}
