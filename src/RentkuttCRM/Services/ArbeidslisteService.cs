using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>
/// En regel som gir en arbeidsoppgave til alle saker som matcher vilkårene.
/// Tomt vilkår = ignoreres. Minst ett vilkår må være satt for at regelen skal telle.
/// </summary>
public class ArbeidsRegel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Oppgave { get; set; } = "";        // teksten som vises som arbeidsoppgave
    public string Status { get; set; } = "";         // "" = alle
    public string Bank { get; set; } = "";           // "" = alle (matcher DelegertBank / «Sendt til bank»)
    public string AvslagGrunn { get; set; } = "";    // "" = alle (delstreng, ikke-sensitiv)
    public string KundeType { get; set; } = "";      // "" / B2C / B2B
    public string Laanetype { get; set; } = "";      // "" = alle
    // Nye vilkår:
    public string NavarendeBank { get; set; } = ""; // kundens nåværende bank (eksakt)
    public string Postnr { get; set; } = "";         // komma-separert; eksakt eller prefiks ("72" = alle 72xx)
    public string Fylke { get; set; } = "";          // fylke (fra kundens fylke eller utledet fra kommune)
    public string Kilde { get; set; } = "";          // leadskilde (eksakt)
    public decimal? BelopMin { get; set; }           // ønsket lånebeløp fra/til
    public decimal? BelopMax { get; set; }
    public bool Aktiv { get; set; } = true;
}

/// <summary>Per sak + regel: kvittert ut («avklart») eller utsatt («ikke svar») til et tidspunkt.</summary>
public class OppgaveTilstand
{
    public string Tilstand { get; set; } = "";   // "avklart" | "utsatt"
    public DateTime? Til { get; set; }            // utsatt til (UTC) — for "utsatt"
}

/// <summary>En aktiv arbeidsoppgave på en sak (regel-id + tekst), i kørekkefølge.</summary>
public class AktivOppgave
{
    public string RegelId { get; set; } = "";
    public string Oppgave { get; set; } = "";
}

/// <summary>Arbeidsliste: regler lagres som JSON i innstillinger (som flyt-boardet).</summary>
public class ArbeidslisteService
{
    private readonly SettingsService _settings;
    private const string Key = "arbeidsliste_regler";
    private const string StatusKey = "arbeidsoppgave_status";   // per sak/regel: avklart/utsatt
    private const string TimerKey = "arbeidsliste_utsatt_timer"; // timer «ikke svar» holdes ute
    public const int StandardUtsattTimer = 24;

    public ArbeidslisteService(SettingsService settings) => _settings = settings;

    public async Task<List<ArbeidsRegel>> HentAsync()
    {
        var json = await _settings.GetAsync(Key);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ArbeidsRegel>>(json) ?? new(); }
        catch { return new(); }
    }

    public Task LagreAsync(List<ArbeidsRegel> regler) =>
        _settings.SetAsync(Key, JsonSerializer.Serialize(regler));

    // ---- Innstilling: timer «ikke svar» holder saken ute av arbeidslisten ----
    public async Task<int> HentUtsattTimerAsync()
    {
        var s = await _settings.GetAsync(TimerKey);
        return int.TryParse(s, out var t) && t > 0 ? t : StandardUtsattTimer;
    }
    public Task LagreUtsattTimerAsync(int timer) =>
        _settings.SetAsync(TimerKey, Math.Max(1, timer).ToString());

    // ---- Tilstand pr. sak/regel (avklart / utsatt) ----
    public async Task<Dictionary<string, Dictionary<string, OppgaveTilstand>>> HentTilstanderAsync()
    {
        var json = await _settings.GetAsync(StatusKey);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, OppgaveTilstand>>>(json) ?? new(); }
        catch { return new(); }
    }

    private Task LagreTilstanderAsync(Dictionary<string, Dictionary<string, OppgaveTilstand>> t) =>
        _settings.SetAsync(StatusKey, JsonSerializer.Serialize(t));

    /// <summary>«Avklart» — oppgaven kvitteres ut permanent for denne saken.</summary>
    public Task SettAvklartAsync(Guid kundekortId, string regelId) =>
        SettTilstandAsync(kundekortId, regelId, new OppgaveTilstand { Tilstand = "avklart" });

    /// <summary>«Ikke svar» — saken ut av lista i angitt antall timer.</summary>
    public async Task SettIkkeSvarAsync(Guid kundekortId, string regelId, int? timer = null)
    {
        var t = timer ?? await HentUtsattTimerAsync();
        await SettTilstandAsync(kundekortId, regelId, new OppgaveTilstand { Tilstand = "utsatt", Til = DateTime.UtcNow.AddHours(t) });
    }

    private async Task SettTilstandAsync(Guid kundekortId, string regelId, OppgaveTilstand tilstand)
    {
        var alle = await HentTilstanderAsync();
        var key = kundekortId.ToString();
        if (!alle.TryGetValue(key, out var forKort)) { forKort = new(); alle[key] = forKort; }
        forKort[regelId] = tilstand;
        await LagreTilstanderAsync(alle);
    }

    /// <summary>Aktive oppgaver på en sak (køen), i regelrekkefølge — hopper over avklarte og utsatte.</summary>
    public static List<AktivOppgave> AktiveOppgaver(Kundekort k, IEnumerable<ArbeidsRegel> regler,
        Dictionary<string, Dictionary<string, OppgaveTilstand>> tilstander, DateTime nowUtc)
    {
        tilstander.TryGetValue(k.Id.ToString(), out var forKort);
        var res = new List<AktivOppgave>();
        foreach (var r in regler)
        {
            if (!r.Aktiv || string.IsNullOrWhiteSpace(r.Oppgave) || !Matcher(r, k)) continue;
            if (forKort != null && forKort.TryGetValue(r.Id, out var t))
            {
                if (t.Tilstand == "avklart") continue;
                if (t.Tilstand == "utsatt" && t.Til.HasValue && t.Til.Value > nowUtc) continue;
            }
            res.Add(new AktivOppgave { RegelId = r.Id, Oppgave = r.Oppgave.Trim() });
        }
        return res;
    }

    /// <summary>Arbeidsoppgavene som gjelder for en sak, ut fra de aktive reglene (uten tilstand).</summary>
    public static List<string> OppgaverFor(Kundekort k, IEnumerable<ArbeidsRegel> regler) =>
        regler.Where(r => r.Aktiv && !string.IsNullOrWhiteSpace(r.Oppgave) && Matcher(r, k))
              .Select(r => r.Oppgave.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    public static bool Matcher(ArbeidsRegel r, Kundekort k)
    {
        var noeSatt = false;
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            noeSatt = true;
            if (!string.Equals(k.Status, r.Status, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.Bank))
        {
            noeSatt = true;
            if (!string.Equals(k.DelegertBank?.Trim(), r.Bank.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.AvslagGrunn))
        {
            noeSatt = true;
            if (string.IsNullOrWhiteSpace(k.AvslagGrunn)
                || k.AvslagGrunn.IndexOf(r.AvslagGrunn.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.KundeType))
        {
            noeSatt = true;
            if (!string.Equals(k.KundeType, r.KundeType, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.Laanetype))
        {
            noeSatt = true;
            if (!string.Equals(k.Laanetype?.Trim(), r.Laanetype.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.NavarendeBank))
        {
            noeSatt = true;
            if (!string.Equals(k.NavarendeBank?.Trim(), r.NavarendeBank.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.Postnr))
        {
            noeSatt = true;
            var pnr = (k.Postnummer ?? "").Trim();
            var tokens = r.Postnr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pnr.Length == 0 || !tokens.Any(t => pnr == t || pnr.StartsWith(t, StringComparison.Ordinal))) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.Fylke))
        {
            noeSatt = true;
            var fylke = !string.IsNullOrWhiteSpace(k.Fylke) ? k.Fylke!.Trim()
                      : !string.IsNullOrWhiteSpace(k.Kommune) ? NorskeKommuner.Fylke(k.Kommune!.Trim()) : "";
            if (!string.Equals(fylke, r.Fylke.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.Kilde))
        {
            noeSatt = true;
            if (!string.Equals(k.Kilde?.Trim(), r.Kilde.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (r.BelopMin.HasValue) { noeSatt = true; if (!(k.OnsketLaanebelop >= r.BelopMin.Value)) return false; }
        if (r.BelopMax.HasValue) { noeSatt = true; if (!(k.OnsketLaanebelop <= r.BelopMax.Value)) return false; }
        return noeSatt;
    }
}
