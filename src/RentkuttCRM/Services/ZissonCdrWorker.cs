namespace RentkuttCRM.Services;

/// <summary>
/// Fyller inn detaljer rundt anropene på dialer-anrop fra Zisson sitt external-statdb (CDR).
/// Et utgående anrop lagres først som «uavklart» (vi vet bare at click-to-call ble satt opp).
/// Noen minutter senere finnes samtalen i Zisson sin CDR (ConversationPeerSessions). Denne jobben
/// matcher på conversationId (== anropets Zid) og setter utfall (svart/ikke svart) + taletid.
///
///  • Kun produksjon (dev/lokalt peker på prod-Supabase og skal ikke jobbe mot Zisson).
///  • Matcher eksakt på Zid → conversationId; taletid &gt; 0 på kunde-benet ⇒ «svart».
///  • Gir opp (utfall «ukjent») på anrop eldre enn <see cref="GiOppEtter"/> som aldri dukket opp i CDR.
/// </summary>
public class ZissonCdrWorker : BackgroundService
{
    private static readonly TimeSpan Intervall = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan Oppslagsvindu = TimeSpan.FromDays(7);   // hvor langt tilbake vi henter uavklarte (for opprydding)
    private static readonly TimeSpan GiOppEtter = TimeSpan.FromHours(12);    // eldre uavklarte gis opp; CDR kommer innen minutter

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZissonCdrWorker> _log;
    private readonly IHostEnvironment _env;

    public ZissonCdrWorker(IServiceScopeFactory scopeFactory, ILogger<ZissonCdrWorker> log, IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_env.IsProduction()) return;
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try { using var scope = _scopeFactory.CreateScope(); await KjorAsync(scope, ct); }
            catch (Exception ex) { _log.LogError(ex, "Zisson CDR-syklus feilet"); }
            try { await Task.Delay(Intervall, ct); } catch { break; }
        }
    }

    private async Task KjorAsync(IServiceScope scope, CancellationToken ct)
    {
        var zisson = scope.ServiceProvider.GetRequiredService<ZissonService>();
        if (!await zisson.AktivertAsync()) return;

        var dialer = scope.ServiceProvider.GetRequiredService<DialerService>();
        var logg = scope.ServiceProvider.GetRequiredService<LoggService>();

        var naa = DateTime.UtcNow;
        var uavklarte = await dialer.UavklarteSidenAsync(naa - Oppslagsvindu);
        if (uavklarte.Count == 0) return;

        // Anrop innenfor matche-vinduet forsøkes matchet mot CDR; eldre gis opp (ukjent).
        var grense = naa - GiOppEtter;
        var nyere = uavklarte.Where(a => a.StartetAt.ToUniversalTime() >= grense).ToList();
        var gamle = uavklarte.Where(a => a.StartetAt.ToUniversalTime() < grense).ToList();

        // Hent CDR-ben for vinduet som dekker de uavklarte anropene (fra eldste – 5 min).
        var perConv = new Dictionary<string, List<ZissonService.CdrBen>>(StringComparer.OrdinalIgnoreCase);
        if (nyere.Count > 0)
        {
            var fra = nyere.Min(a => a.StartetAt).ToUniversalTime().AddMinutes(-5);
            var ben = await zisson.HentCdrBenAsync(fra, naa.AddMinutes(2));
            foreach (var g in ben.Where(b => !string.IsNullOrWhiteSpace(b.ConversationId))
                                  .GroupBy(b => b.ConversationId!, StringComparer.OrdinalIgnoreCase))
                perConv[g.Key] = g.ToList();
        }

        var oppdatert = 0;
        foreach (var a in nyere)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(a.Zid) || !perConv.TryGetValue(a.Zid!, out var legs) || legs.Count == 0)
                continue;   // ikke i CDR ennå — prøv igjen neste syklus

            // Kunde-benet (ekstern part) bærer taletiden; fall tilbake til høyeste taletid blant benene.
            var kundeben = legs.Where(l => l.IsExternalPeer).ToList();
            var taletid = (kundeben.Count > 0 ? kundeben : legs).Max(l => l.TaletidSek);
            var utfall = taletid > 0 ? DialerService.UtfallSvart : DialerService.UtfallIkkeSvart;

            await dialer.SettResultatAsync(a.Id, DialerService.StatusFerdig, utfall, taletid, naa);
            await logg.LoggAsync(a.KundekortId, "System (Zisson CDR)",
                utfall == DialerService.UtfallSvart
                    ? $"📞 Utgående samtale besvart · {FmtTid(taletid)} taletid"
                    : "📞 Utgående anrop – ikke besvart",
                kategori: "anrop");
            oppdatert++;
        }

        // Gi opp på anrop som aldri dukket opp i CDR innen fristen.
        foreach (var a in gamle)
        {
            if (ct.IsCancellationRequested) break;
            await dialer.SettResultatAsync(a.Id, DialerService.StatusFerdig, DialerService.UtfallUkjent, null, naa);
        }

        if (oppdatert > 0 || gamle.Count > 0)
            _log.LogInformation("Zisson CDR: {Oppdatert} anrop fylt fra CDR, {Gamle} gitt opp (ukjent).", oppdatert, gamle.Count);
    }

    private static string FmtTid(int sek) => sek >= 60 ? $"{sek / 60} min {sek % 60} s" : $"{sek} s";
}
