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
    public string PostnrFra { get; set; } = "";       // intervall fra (4-sifret)
    public string PostnrTil { get; set; } = "";       // intervall til (4-sifret)
    public string Fylke { get; set; } = "";          // fylke (fra kundens fylke eller utledet fra kommune)
    public string Kilde { get; set; } = "";          // leadskilde (eksakt)
    public decimal? BelopMin { get; set; }           // ønsket lånebeløp fra/til
    public decimal? BelopMax { get; set; }
    // Ekskludering: hvis saken OGSÅ matcher disse vilkårene, gis IKKE oppgaven (unngår overlapp
    // mellom regler — f.eks. «ring Akershus + Oslo» men ekskluder fylke Oslo så de ikke dobles).
    public ArbeidsVilkaar Ekskluder { get; set; } = new();
    public bool Aktiv { get; set; } = true;
}

/// <summary>Et sett med match-vilkår (brukes til ekskludering på en arbeidsregel). Tomt = ingen.</summary>
public class ArbeidsVilkaar
{
    public string Status { get; set; } = "";
    public string Bank { get; set; } = "";
    public string NavarendeBank { get; set; } = "";
    public string Postnr { get; set; } = "";
    public string PostnrFra { get; set; } = "";
    public string PostnrTil { get; set; } = "";
    public string Fylke { get; set; } = "";
    public string Kilde { get; set; } = "";
    public string AvslagGrunn { get; set; } = "";
    public string KundeType { get; set; } = "";
    public string Laanetype { get; set; } = "";
    public decimal? BelopMin { get; set; }
    public decimal? BelopMax { get; set; }
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

    /// <summary>Normalisert nøkkel for en oppgave — avklart/utsatt spores pr. oppgavetekst (ikke pr. regel),
    /// slik at overlappende regler med SAMME oppgave deler tilstand og ikke dobles.</summary>
    public static string NormOppgave(string? oppgave) => (oppgave ?? "").Trim().ToLowerInvariant();

    /// <summary>«Avklart» — oppgaven kvitteres ut permanent for denne saken.</summary>
    public Task SettAvklartAsync(Guid kundekortId, string oppgave) =>
        SettTilstandAsync(kundekortId, NormOppgave(oppgave), new OppgaveTilstand { Tilstand = "avklart" });

    /// <summary>«Ikke svar» — saken ut av lista i angitt antall timer.</summary>
    public async Task SettIkkeSvarAsync(Guid kundekortId, string oppgave, int? timer = null)
    {
        var t = timer ?? await HentUtsattTimerAsync();
        await SettTilstandAsync(kundekortId, NormOppgave(oppgave), new OppgaveTilstand { Tilstand = "utsatt", Til = DateTime.UtcNow.AddHours(t) });
    }

    private async Task SettTilstandAsync(Guid kundekortId, string oppgaveKey, OppgaveTilstand tilstand)
    {
        var alle = await HentTilstanderAsync();
        var key = kundekortId.ToString();
        if (!alle.TryGetValue(key, out var forKort)) { forKort = new(); alle[key] = forKort; }
        forKort[oppgaveKey] = tilstand;
        await LagreTilstanderAsync(alle);
    }

    /// <summary>Den aktive arbeidsoppgaven på en sak — AUTOMATISK én om gangen (første regel som matcher i
    /// rekkefølge vinner). Overlappende regler lager derfor aldri doble oppgaver; når den aktive kvitteres ut
    /// (avklart/utsatt) rykker neste matchende regel automatisk opp. Avklart/utsatt spores pr. oppgavetekst.</summary>
    public static List<AktivOppgave> AktiveOppgaver(Kundekort k, IEnumerable<ArbeidsRegel> regler,
        Dictionary<string, Dictionary<string, OppgaveTilstand>> tilstander, DateTime nowUtc)
    {
        tilstander.TryGetValue(k.Id.ToString(), out var forKort);
        foreach (var r in regler)
        {
            if (!r.Aktiv || string.IsNullOrWhiteSpace(r.Oppgave) || !Matcher(r, k)) continue;
            var tekst = r.Oppgave.Trim();
            if (forKort != null && forKort.TryGetValue(NormOppgave(tekst), out var t))
            {
                if (t.Tilstand == "avklart") continue;
                if (t.Tilstand == "utsatt" && t.Til.HasValue && t.Til.Value > nowUtc) continue;
            }
            // Første matchende, ikke-avklarte/ikke-utsatte regel vinner — kun én aktiv oppgave per sak.
            return new List<AktivOppgave> { new() { RegelId = r.Id, Oppgave = tekst } };
        }
        return new List<AktivOppgave>();
    }

    /// <summary>Arbeidsoppgavene som gjelder for en sak, ut fra de aktive reglene (uten tilstand).</summary>
    public static List<string> OppgaverFor(Kundekort k, IEnumerable<ArbeidsRegel> regler) =>
        regler.Where(r => r.Aktiv && !string.IsNullOrWhiteSpace(r.Oppgave) && Matcher(r, k))
              .Select(r => r.Oppgave.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    public static bool Matcher(ArbeidsRegel r, Kundekort k)
    {
        var inkl = new ArbeidsVilkaar
        {
            Status = r.Status, Bank = r.Bank, NavarendeBank = r.NavarendeBank,
            Postnr = r.Postnr, PostnrFra = r.PostnrFra, PostnrTil = r.PostnrTil,
            Fylke = r.Fylke, Kilde = r.Kilde, AvslagGrunn = r.AvslagGrunn,
            KundeType = r.KundeType, Laanetype = r.Laanetype, BelopMin = r.BelopMin, BelopMax = r.BelopMax,
        };
        // Inkluderingsvilkår: minst ett må være satt, og alle satte må matche.
        if (!MatcherVilkaar(inkl, k, out var noeSatt) || !noeSatt) return false;
        // Ekskludering: hvis ekskluderingsvilkår er satt OG saken matcher dem → ingen oppgave.
        if (r.Ekskluder is { } e && MatcherVilkaar(e, k, out var ekskSatt) && ekskSatt) return false;
        return true;
    }

    /// <summary>True hvis saken matcher alle SATTE vilkår i v (vakuøst true når ingen er satt).
    /// <paramref name="noeSatt"/> = om minst ett vilkår var satt.</summary>
    public static bool MatcherVilkaar(ArbeidsVilkaar v, Kundekort k, out bool noeSatt)
    {
        noeSatt = false;
        if (!string.IsNullOrWhiteSpace(v.Status))
        {
            noeSatt = true;
            if (!string.Equals(k.Status, v.Status, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.Bank))
        {
            noeSatt = true;
            if (!string.Equals(k.DelegertBank?.Trim(), v.Bank.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.AvslagGrunn))
        {
            noeSatt = true;
            if (string.IsNullOrWhiteSpace(k.AvslagGrunn)
                || k.AvslagGrunn.IndexOf(v.AvslagGrunn.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.KundeType))
        {
            noeSatt = true;
            if (!string.Equals(k.KundeType, v.KundeType, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.Laanetype))
        {
            noeSatt = true;
            if (!string.Equals(k.Laanetype?.Trim(), v.Laanetype.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.NavarendeBank))
        {
            noeSatt = true;
            if (!string.Equals(k.NavarendeBank?.Trim(), v.NavarendeBank.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.Postnr))
        {
            noeSatt = true;
            var pnr = (k.Postnummer ?? "").Trim();
            var tokens = v.Postnr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pnr.Length == 0 || !tokens.Any(t => pnr == t || pnr.StartsWith(t, StringComparison.Ordinal))) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.PostnrFra) || !string.IsNullOrWhiteSpace(v.PostnrTil))
        {
            noeSatt = true;
            if (!int.TryParse((k.Postnummer ?? "").Trim(), out var pn)) return false;
            if (int.TryParse(v.PostnrFra.Trim(), out var fra) && pn < fra) return false;
            if (int.TryParse(v.PostnrTil.Trim(), out var til) && pn > til) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.Fylke))
        {
            noeSatt = true;
            var fylke = !string.IsNullOrWhiteSpace(k.Fylke) ? k.Fylke!.Trim()
                      : !string.IsNullOrWhiteSpace(k.Kommune) ? NorskeKommuner.Fylke(k.Kommune!.Trim()) : "";
            if (!string.Equals(fylke, v.Fylke.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(v.Kilde))
        {
            noeSatt = true;
            if (!string.Equals(k.Kilde?.Trim(), v.Kilde.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (v.BelopMin.HasValue) { noeSatt = true; if (!(k.OnsketLaanebelop >= v.BelopMin.Value)) return false; }
        if (v.BelopMax.HasValue) { noeSatt = true; if (!(k.OnsketLaanebelop <= v.BelopMax.Value)) return false; }
        return true;
    }
}
