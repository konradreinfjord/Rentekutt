namespace RentkuttCRM.Services;

/// <summary>
/// Kobler Instabank-API-hendelsen «søknad innvilget» til de hendelsesbaserte SMS-løpene
/// (<see cref="SmsLoep.VedInnvilgelse"/>). Når statussynken markerer en Instabank-sak som
/// «Innvilget», fyres hvert aktivt innvilgelse-løp: SMS (valgfri mal) + valgfri Klaviyo-hendelse
/// (e-post-flow). Konfigureres som et vanlig SMS-løp i Kommunikasjon-fanen. Sender kun én gang
/// per sak per løp (dedup), med riktig kundes signeringslenke flettet inn.
/// </summary>
public class InstabankInnvilgetEventService
{
    public const string StandardKlaviyoEvent = "Innvilget i Instabank";

    private readonly SmsLoepService _loep;
    private readonly SmsMalService _sms;
    private readonly SmsUtsendingService _utsending;
    private readonly KlaviyoService _klaviyo;
    private readonly ILogger<InstabankInnvilgetEventService> _log;

    public InstabankInnvilgetEventService(SmsLoepService loep, SmsMalService sms,
        SmsUtsendingService utsending, KlaviyoService klaviyo, ILogger<InstabankInnvilgetEventService> log)
    {
        _loep = loep;
        _sms = sms;
        _utsending = utsending;
        _klaviyo = klaviyo;
        _log = log;
    }

    /// <summary>Kalles ved overgangen til «Innvilget» fra Instabank-API-et. Fyrer alle aktive
    /// innvilgelse-løp (SMS + Klaviyo). Idempotent: hvert løp sender kun én gang per sak.</summary>
    public async Task FyrInnvilgetAsync(Kundekort k, string? signeringslenke)
    {
        try
        {
            if (k is null) return;
            var loep = (await _loep.HentAsync())
                .Where(l => l.Aktiv && l.VedInnvilgelse && !string.IsNullOrWhiteSpace(l.MalNavn))
                .ToList();
            if (loep.Count == 0) return;

            var maler = await _sms.ListAsync();

            foreach (var l in loep)
            {
                var type = "sms_loep:" + l.Id;                 // samme dedup-nøkkel som tidsbaserte løp
                if (await _utsending.HarSendtOkAsync(k.Id, type)) continue;

                var mal = maler.FirstOrDefault(m => m.Navn == l.MalNavn);
                if (mal is null) { _log.LogWarning("Innvilgelse-løp «{Navn}»: fant ikke mal «{Mal}».", l.Navn, l.MalNavn); continue; }

                // SMS
                if (!string.IsNullOrWhiteSpace(k.Mobilnummer))
                {
                    var melding = SmsMalService.Flett(mal.Tekst, k, signeringslenke);
                    var (ok, detalj) = await _sms.SendRaaAsync(k.Mobilnummer, melding);
                    await _utsending.LoggAsync(k.Id, type, k.Mobilnummer, ok, detalj);
                    if (!ok) _log.LogWarning("Innvilgelse-løp «{Navn}» SMS feilet for {Id}: {Detalj}", l.Navn, k.Id, detalj);
                }
                else
                {
                    // Ingen mobil: logg dedup-spor likevel så Klaviyo ikke fyres på nytt.
                    await _utsending.LoggAsync(k.Id, type, null, true, "Ingen mobil — kun Klaviyo");
                }

                // Klaviyo-event → e-post-flow (valgfritt).
                if (!string.IsNullOrWhiteSpace(l.KlaviyoEvent))
                    _klaviyo.Fyr(k, l.KlaviyoEvent!, SmsMalService.FletteEgenskaper(k, signeringslenke));

                _log.LogInformation("Innvilgelse-løp «{Navn}» fyrt for sak {Id}.", l.Navn, k.Id);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Instabank innvilgelse-event feilet for {Id}", k?.Id); }
    }
}
