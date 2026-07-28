using System.Text.Json;
using RentkuttCRM.Controllers;

namespace RentkuttCRM.Services;

/// <summary>
/// Re-kjøring av en lagret webhook-payload (fra «siste 50»-lista) — så et lead som feilet ved mottak
/// kan opprettes i ettertid uten at kunden må sende inn på nytt. Gjenbruker samme mapping som
/// live-mottaket (<see cref="WebhookController.MapFlexible"/> / Flatten / KildeLabel).
///
/// Merk: den lagrede payloaden har fødselsnummer MASKERT (personvern). For prismatch-leads (uten fnr)
/// gjenskapes alt; for skjema-leads med fnr gjenskapes kontakt/øvrig, men fnr må fylles inn manuelt
/// (maskert verdi tolkes som «mangler», ikke lagres som fnr).
/// </summary>
public class LeadMottakService
{
    private readonly KundekortService _kundekort;
    private readonly SamtykkeService _samtykke;
    private readonly WebhookPayloadService _payloads;
    private readonly LoggService _logg;
    private readonly ILogger<LeadMottakService> _log;

    public LeadMottakService(KundekortService kundekort, SamtykkeService samtykke,
        WebhookPayloadService payloads, LoggService logg, ILogger<LeadMottakService> log)
    {
        _kundekort = kundekort;
        _samtykke = samtykke;
        _payloads = payloads;
        _logg = logg;
        _log = log;
    }

    public record Resultat(bool Ok, int Opprettet, string? Feil);

    /// <summary>Re-kjør en lagret payload og opprett lead(ene). Marker payloaden OK ved suksess.</summary>
    public async Task<Resultat> ReprosseserAsync(Guid payloadId, string? aktor)
    {
        var p = await _payloads.HentAsync(payloadId);
        if (p is null) return new(false, 0, "Fant ikke payloaden.");
        if (string.IsNullOrWhiteSpace(p.Payload)) return new(false, 0, "Payloaden er tom.");

        var kanal = p.Kanal ?? WebhookService.InboundName;
        var erPrismatch = kanal == WebhookService.PrismatchName;

        JsonElement body;
        try { body = JsonDocument.Parse(p.Payload).RootElement; }
        catch (Exception ex) { return new(false, 0, "Ugyldig JSON i payloaden: " + ex.Message); }

        var elements = body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().ToList()
            : new List<JsonElement> { body };

        int opprettet = 0; string? feil = null; Guid? forsteId = null;
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            try
            {
                var flat = WebhookController.Flatten(el);
                var k = WebhookController.MapFlexible(flat);
                k.Kilde = WebhookController.KildeLabel(kanal);
                if (erPrismatch) k.Status = KundekortService.StatusPaabegynt;

                // Maskert/ugyldig fnr fra bufferet skal ikke lagres som fnr — blank det (må fylles manuelt).
                if (!string.IsNullOrWhiteSpace(k.Foedselsnummer) &&
                    new string(k.Foedselsnummer.Where(char.IsDigit).ToArray()).Length != 11)
                    k.Foedselsnummer = null;

                var (ok, error) = await _kundekort.SaveAsync(k, aktor: aktor ?? "Re-kjørt fra payload");
                if (!ok) { feil = error; continue; }

                if (k.SamtykkeGjeldsregisterKredittsjekk && k.Id != Guid.Empty)
                    await _samtykke.RegistrerAsync(k.Id, SamtykkeService.FormaalKreditt, WebhookController.KildeLabel(kanal),
                        tekstversjon: SamtykkeService.SamtykketekstVersjon);

                await _logg.LoggAsync(k.Id, aktor, $"Lead opprettet ved re-kjøring av lagret payload ({kanal}).", kategori: "kobling");
                opprettet++;
                forsteId ??= k.Id;
            }
            catch (Exception ex) { feil = ex.Message; _log.LogWarning(ex, "Re-kjøring av payload feilet"); }
        }

        if (opprettet > 0)
        {
            await _payloads.MarkerOkAsync(payloadId, forsteId);
            return new(true, opprettet, null);
        }
        return new(false, 0, feil ?? "Ingen gyldige leads i payloaden.");
    }
}
