using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("webhook_payload")]
public class WebhookPayload : BaseModel
{
    [PrimaryKey("id", false)] public Guid Id { get; set; }
    [Column("kanal")] public string? Kanal { get; set; }
    [Column("payload")] public string? Payload { get; set; }
    [Column("ok")] public bool Ok { get; set; } = true;
    [Column("feil")] public string? Feil { get; set; }
    [Column("kundekort_id")] public Guid? KundekortId { get; set; }
    [Column("mottatt", ignoreOnInsert: true)] public DateTime Mottatt { get; set; }
}

/// <summary>
/// Lagrer innkommende webhook-payloads for feilsøking. Beholder kun de siste <see cref="MaksBevart"/>
/// (trimmes ved skriving). Fødselsnummer maskeres av kalleren (FnrRedactor) før lagring.
/// Feiler aldri hardt — lagring skal ikke kunne velte webhook-mottaket.
/// </summary>
public class WebhookPayloadService
{
    public const int MaksBevart = 50;
    /// <summary>Payloads eldre enn dette slettes (tidsbasert, i tillegg til 50-taket).</summary>
    public const int RetensjonDager = 10;

    private readonly Supabase.Client _client;
    private readonly ILogger<WebhookPayloadService> _log;
    public bool IsConfigured { get; }

    private static readonly List<WebhookPayload> _staging = new();
    private bool _initialized;

    public WebhookPayloadService(Supabase.Client client, IConfiguration cfg, ILogger<WebhookPayloadService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task LagreAsync(string? kanal, string? payload, bool ok, string? feil, Guid? kundekortId)
    {
        var rad = new WebhookPayload { Kanal = kanal, Payload = payload, Ok = ok, Feil = feil, KundekortId = kundekortId };
        if (!IsConfigured)
        {
            rad.Id = Guid.NewGuid(); rad.Mottatt = DateTime.UtcNow;
            _staging.Insert(0, rad);
            if (_staging.Count > MaksBevart) _staging.RemoveRange(MaksBevart, _staging.Count - MaksBevart);
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<WebhookPayload>().Insert(rad);
            // Trim: behold kun de nyeste MaksBevart radene.
            var alle = (await _client.From<WebhookPayload>()
                .Select("id")
                .Order(x => x.Mottatt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Get()).Models;
            foreach (var gammel in alle.Skip(MaksBevart))
                await _client.From<WebhookPayload>().Where(x => x.Id == gammel.Id).Delete();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Lagring av webhook-payload feilet"); }
    }

    public async Task<List<WebhookPayload>> SisteAsync(int antall = MaksBevart)
    {
        if (!IsConfigured) return _staging.Take(antall).ToList();
        try
        {
            await EnsureInitAsync();
            return (await _client.From<WebhookPayload>()
                .Order(x => x.Mottatt, Constants.Ordering.Descending, Constants.NullPosition.Last)
                .Limit(antall)
                .Get()).Models;
        }
        catch (Exception ex) { _log.LogError(ex, "Henting av webhook-payloads feilet"); return new(); }
    }

    /// <summary>Hent én payload (for re-kjøring).</summary>
    public async Task<WebhookPayload?> HentAsync(Guid id)
    {
        if (!IsConfigured) return _staging.FirstOrDefault(x => x.Id == id);
        try
        {
            await EnsureInitAsync();
            return (await _client.From<WebhookPayload>().Where(x => x.Id == id).Get()).Models.FirstOrDefault();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Henting av payload feilet"); return null; }
    }

    /// <summary>Marker en payload som vellykket etter re-kjøring (setter ok=true, fjerner feil, kobler til lead).</summary>
    public async Task MarkerOkAsync(Guid id, Guid? kundekortId)
    {
        if (!IsConfigured)
        {
            var r = _staging.FirstOrDefault(x => x.Id == id);
            if (r is not null) { r.Ok = true; r.Feil = null; r.KundekortId = kundekortId; }
            return;
        }
        try
        {
            await EnsureInitAsync();
            await _client.From<WebhookPayload>().Where(x => x.Id == id)
                .Set(x => x.Ok, true).Set(x => x.Feil!, (string?)null).Set(x => x.KundekortId!, kundekortId).Update();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Marker payload OK feilet"); }
    }

    /// <summary>Slett payloads eldre enn <paramref name="dager"/> dager. Returnerer antall slettet.</summary>
    public async Task<int> SlettEldreEnnAsync(int dager)
    {
        if (dager < 1) dager = 1;
        var grense = DateTime.UtcNow.AddDays(-dager);
        if (!IsConfigured) return _staging.RemoveAll(x => x.Mottatt < grense);
        try
        {
            await EnsureInitAsync();
            var alle = (await _client.From<WebhookPayload>().Get()).Models;
            var gamle = alle.Where(x => x.Mottatt < grense).ToList();
            foreach (var g in gamle)
                await _client.From<WebhookPayload>().Where(x => x.Id == g.Id).Delete();
            return gamle.Count;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Sletting av gamle webhook-payloads feilet"); return 0; }
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
