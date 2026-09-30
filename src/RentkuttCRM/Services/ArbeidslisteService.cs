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
    public bool Aktiv { get; set; } = true;
}

/// <summary>Arbeidsliste: regler lagres som JSON i innstillinger (som flyt-boardet).</summary>
public class ArbeidslisteService
{
    private readonly SettingsService _settings;
    private const string Key = "arbeidsliste_regler";

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

    /// <summary>Arbeidsoppgavene som gjelder for en sak, ut fra de aktive reglene.</summary>
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
        return noeSatt;
    }
}
