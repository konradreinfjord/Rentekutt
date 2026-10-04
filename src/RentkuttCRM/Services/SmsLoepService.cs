using System.Text.Json;

namespace RentkuttCRM.Services;

/// <summary>Ett konfigurerbart SMS-løp: når en sak har stått i en gitt status i X timer (uten å ha
/// byttet status), sendes en mal-SMS — én gang per sak. Eksempel: «Påbegynt søknad» i 1 time → SMS,
/// og et eget løp for 24 timer. Utsending skjer kun innenfor sendevinduet (08–21 på hverdager).</summary>
public class SmsLoep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Navn { get; set; } = "";
    public bool Aktiv { get; set; }
    /// <summary>Status saken må stå i (trigger + betingelse: fortsatt i denne statusen når det sendes).</summary>
    public string TriggerStatus { get; set; } = KundekortService.StatusPaabegynt;
    /// <summary>Timer etter at saken ble registrert før SMS sendes (målt fra opprettet).</summary>
    public int SendEtterTimer { get; set; } = 24;
    /// <summary>Ikke send til saker eldre enn dette (hindrer masseutsending når løpet skrus på).</summary>
    public int IkkeEldreTimer { get; set; } = 72;
    public string MalNavn { get; set; } = "";
}

/// <summary>Lagrer/henter SMS-løp (JSON i innstillinger). Seeder fra det gamle 24t-oppsettet
/// første gang, slik at eksisterende automatikk bevares.</summary>
public class SmsLoepService
{
    public const string Key = "sms_loep";
    private readonly SettingsService _settings;

    public SmsLoepService(SettingsService settings) => _settings = settings;

    public async Task<List<SmsLoep>> HentAsync()
    {
        var raw = await _settings.GetAsync(Key);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { return JsonSerializer.Deserialize<List<SmsLoep>>(raw) ?? new(); }
            catch { /* faller tilbake til seed under */ }
        }

        // Bakoverkompatibelt: lag ett løp fra det gamle 24t-oppsettet hvis en mal var valgt.
        var mal = await _settings.GetAsync(Paamindelse24tWorker.KeyMal);
        if (!string.IsNullOrWhiteSpace(mal))
        {
            return new List<SmsLoep>
            {
                new()
                {
                    Navn = "24-timers påminnelse",
                    Aktiv = await _settings.GetBoolAsync(Paamindelse24tWorker.KeyEnabled, false),
                    TriggerStatus = KundekortService.StatusPaabegynt,
                    SendEtterTimer = await _settings.GetIntAsync(Paamindelse24tWorker.KeyMinTimer, 24),
                    IkkeEldreTimer = await _settings.GetIntAsync(Paamindelse24tWorker.KeyMaksTimer, 72),
                    MalNavn = mal!,
                },
            };
        }
        return new();
    }

    public Task LagreAsync(List<SmsLoep> loep)
        => _settings.SetAsync(Key, JsonSerializer.Serialize(loep));
}
