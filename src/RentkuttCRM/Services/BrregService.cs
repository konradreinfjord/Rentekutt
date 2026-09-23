using System.Net;
using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// Oppslag mot Brønnøysundregistrenes åpne Enhetsregister (data.brreg.no). Kalles server-side
/// (Blazor Server), siden nettleseren er CSP-sperret mot eksterne kall. Gir konkurs-/avviklings-
/// status + nøkkelfakta. Merk: heftelser/pant ligger i Løsøreregisteret (tilgangsstyrt) og er
/// IKKE tilgjengelig her.
/// </summary>
public class BrregService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BrregService> _log;

    public BrregService(IHttpClientFactory http, ILogger<BrregService> log)
    {
        _http = http;
        _log = log;
    }

    public record Enhet(
        string Orgnr, string Navn, string? Organisasjonsform,
        bool Konkurs, bool UnderAvvikling, bool UnderTvangsavvikling,
        string? Naeringskode, int? AntallAnsatte, string? Stiftelsesdato,
        string? Adresse, string? Hjemmeside, string? SisteAarsregnskap, string? RegistrertDato)
    {
        // Én eller flere risikoflagg er satt.
        public bool HarRisiko => Konkurs || UnderAvvikling || UnderTvangsavvikling;
    }

    public record Resultat(bool Ok, string? Feil, Enhet? Enhet, List<Enhet> Treff);

    /// <summary>Slår opp et foretak: har vi 9-sifret orgnr hentes det direkte, ellers søkes på navn
    /// (inntil 5 treff returneres i <see cref="Resultat.Treff"/>).</summary>
    public async Task<Resultat> SlaaOppAsync(string? orgnr, string? navn, CancellationToken ct = default)
    {
        var org = new string((orgnr ?? "").Where(char.IsDigit).ToArray());
        try
        {
            var c = _http.CreateClient("brreg");
            c.Timeout = TimeSpan.FromSeconds(15);

            if (org.Length == 9)
            {
                using var res = await c.GetAsync($"enhetsregisteret/api/enheter/{org}", ct);
                if (res.StatusCode == HttpStatusCode.NotFound)
                    return new(false, $"Fant ikke org.nr {org} i Enhetsregisteret.", null, new());
                if (!res.IsSuccessStatusCode)
                    return new(false, $"Brønnøysund svarte HTTP {(int)res.StatusCode}.", null, new());
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var e = Parse(doc.RootElement);
                return new(true, null, e, new() { e });
            }

            if (string.IsNullOrWhiteSpace(navn))
                return new(false, "Mangler både org.nr og firmanavn å slå opp.", null, new());

            using var r2 = await c.GetAsync($"enhetsregisteret/api/enheter?navn={Uri.EscapeDataString(navn.Trim())}&size=5", ct);
            if (!r2.IsSuccessStatusCode)
                return new(false, $"Brønnøysund svarte HTTP {(int)r2.StatusCode}.", null, new());
            using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(ct));
            var liste = new List<Enhet>();
            if (d2.RootElement.TryGetProperty("_embedded", out var emb) && emb.TryGetProperty("enheter", out var arr))
                foreach (var el in arr.EnumerateArray()) liste.Add(Parse(el));

            if (liste.Count == 0) return new(false, $"Ingen treff på «{navn.Trim()}» i Enhetsregisteret.", null, new());

            // Eksakt navnetreff (eller ett treff) vises som detaljert kort; ellers en trefferliste.
            var eksakt = liste.FirstOrDefault(x => string.Equals(x.Navn, navn.Trim(), StringComparison.OrdinalIgnoreCase));
            var valgt = eksakt ?? (liste.Count == 1 ? liste[0] : null);
            return new(true, null, valgt, liste);
        }
        catch (TaskCanceledException) { return new(false, "Tidsavbrudd mot Brønnøysund (svarte ikke innen 15 s).", null, new()); }
        catch (Exception ex) { _log.LogWarning(ex, "Brreg-oppslag feilet"); return new(false, "Teknisk feil ved oppslag: " + ex.Message, null, new()); }
    }

    private static Enhet Parse(JsonElement d)
    {
        string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool B(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        int? I(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        string? Beskrivelse(string obj) => d.TryGetProperty(obj, out var o) && o.TryGetProperty("beskrivelse", out var b) ? b.GetString() : null;

        string? Adresse()
        {
            if (!d.TryGetProperty("forretningsadresse", out var a)) return null;
            var linjer = new List<string>();
            if (a.TryGetProperty("adresse", out var l) && l.ValueKind == JsonValueKind.Array)
                linjer.AddRange(l.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)));
            var postnr = a.TryGetProperty("postnummer", out var pn) ? pn.GetString() : null;
            var poststed = a.TryGetProperty("poststed", out var ps) ? ps.GetString() : null;
            var post = $"{postnr} {poststed}".Trim();
            var deler = linjer.ToList();
            if (post.Length > 0) deler.Add(post);
            return deler.Count > 0 ? string.Join(", ", deler) : null;
        }

        return new Enhet(
            S("organisasjonsnummer") ?? "",
            S("navn") ?? "",
            Beskrivelse("organisasjonsform"),
            B("konkurs"), B("underAvvikling"), B("underTvangsavviklingEllerTvangsopplosning"),
            Beskrivelse("naeringskode1"), I("antallAnsatte"), S("stiftelsesdato"),
            Adresse(), S("hjemmeside"), S("sisteInnsendteAarsregnskap"), S("registreringsdatoEnhetsregisteret"));
    }
}
