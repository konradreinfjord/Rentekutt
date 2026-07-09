namespace RentkuttCRM.Services;

/// <summary>
/// Norsk tid (Europe/Oslo). Serveren (Azure) kjører i UTC, så <c>DateTime.Now</c> og
/// <c>ToLocalTime()</c> gir UTC — 1–2 timer bak norsk tid. Bruk denne til all visning
/// og dato-gruppering slik at «nå», «i dag» og klokkeslett stemmer med Norge.
/// </summary>
public static class Tid
{
    private static readonly TimeZoneInfo Oslo = FinnOslo();

    private static TimeZoneInfo FinnOslo()
    {
        // IANA («Europe/Oslo») på Linux/macOS, Windows-id som fallback.
        foreach (var id in new[] { "Europe/Oslo", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* prøv neste */ }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Nåtidspunktet i norsk tid.</summary>
    public static DateTime Naa => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Oslo);

    /// <summary>Dagens dato i norsk tid.</summary>
    public static DateTime IDag => Naa.Date;

    /// <summary>
    /// Konverter et tidsstempel til norsk tid. Verdier fra databasen (timestamptz) kommer
    /// som UTC eller Unspecified — begge tolkes som UTC-øyeblikk.
    /// </summary>
    public static DateTime TilOslo(this DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Oslo);
    }
}
