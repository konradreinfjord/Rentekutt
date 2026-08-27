namespace RentkuttCRM.Services;

/// <summary>
/// Liste over fagforeninger som kan velges på kundekortet. Standardsettet er alltid
/// tilgjengelig; nye fagforeninger legges til av agentene og lagres delt i innstillinger
/// (nøkkel <see cref="Key"/>) slik at de dukker opp for alle kundekort etterpå.
/// </summary>
public class FagforeningService
{
    public const string Key = "fagforeninger";

    /// <summary>Forhåndsdefinerte fagforeninger — vises alltid øverst i lista.</summary>
    public static readonly string[] Standard = { "Cona", "Tekna", "Akademikerne", "NSF", "LO favør" };

    private readonly SettingsService _settings;
    public FagforeningService(SettingsService settings) => _settings = settings;

    private static List<string> Parse(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new()
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Full liste: standardsettet først, deretter egendefinerte (uten duplikater).</summary>
    public async Task<List<string>> HentAsync()
    {
        var lagt = Parse(await _settings.GetAsync(Key));
        return Standard
            .Concat(lagt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Legger til en ny fagforening (hvis den ikke finnes fra før) og returnerer oppdatert liste.</summary>
    public async Task<List<string>> LeggTilAsync(string navn)
    {
        navn = (navn ?? "").Trim();
        if (navn.Length == 0) return await HentAsync();

        var finnes = Standard.Concat(Parse(await _settings.GetAsync(Key)))
            .Any(x => string.Equals(x, navn, StringComparison.OrdinalIgnoreCase));
        if (!finnes)
        {
            var lagt = Parse(await _settings.GetAsync(Key));
            lagt.Add(navn);
            await _settings.SetAsync(Key, string.Join("\n", lagt));
        }
        return await HentAsync();
    }
}
