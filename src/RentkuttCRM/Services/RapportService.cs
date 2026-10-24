namespace RentkuttCRM.Services;

/// <summary>
/// Aggregert arbeidsrapport for ledelsen — eksponeres via Tredjepart-API-et (GET /api/tredjepart/rapport)
/// og forhåndsvises i Admin. Inneholder KUN tall og fordelinger (antall, summer, snitt per kilde/type/
/// agent/bank). Aldri navn, telefon, fødselsnummer, beløp på enkeltsaker eller annen kundedata.
///
/// Øyeblikksbildet («naa») er ikke datostyrt; alt annet gjelder perioden fra–til (Oslo-datoer, inkl.).
/// Agent-filter: saker eid av agenten (eier = e-post) og handlinger utført av agenten (aktør = navn/e-post).
/// </summary>
public class RapportService
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private readonly KundekortService _kort;
    private readonly LoggService _logg;
    private readonly DialerService _dialer;
    private readonly BankSendingService _bank;
    private readonly SupabaseUserService _users;

    public RapportService(KundekortService kort, LoggService logg, DialerService dialer, BankSendingService bank, SupabaseUserService users)
    {
        _kort = kort;
        _logg = logg;
        _dialer = dialer;
        _bank = bank;
        _users = users;
    }

    public static readonly string[] Kundetyper = { "B2C", "B2B" };

    public async Task<List<UserRow>> AgenterAsync()
    {
        try { return (await _users.ListUsersAsync()).Where(u => u.Active).OrderBy(u => u.FullName).ToList(); }
        catch { return new(); }
    }

    /// <summary>Finn agent på e-post (eksakt) eller navn (eksakt, ellers delstreng hvis entydig).</summary>
    public static UserRow? FinnAgent(IEnumerable<UserRow> brukere, string? sok)
    {
        if (string.IsNullOrWhiteSpace(sok)) return null;
        var s = sok.Trim();
        var liste = brukere.ToList();
        return liste.FirstOrDefault(u => u.Email.Equals(s, OIC))
            ?? liste.FirstOrDefault(u => u.FullName.Equals(s, OIC))
            ?? (liste.Count(u => u.FullName.Contains(s, OIC)) == 1 ? liste.First(u => u.FullName.Contains(s, OIC)) : null);
    }

    public async Task<object> LagAsync(DateOnly fra, DateOnly til, UserRow? agent, string? kundetype)
    {
        if (fra > til) (fra, til) = (til, fra);
        kundetype = string.IsNullOrWhiteSpace(kundetype) ? null : kundetype.Trim().ToUpperInvariant();

        // Hent med én dags margin og filtrer på Oslo-dato i minnet (DB lagrer UTC).
        var fraUtc = fra.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        bool IPeriode(DateTime utc) { var d = DateOnly.FromDateTime(utc.TilOslo()); return d >= fra && d <= til; }

        var alle = await _kort.ListLettAsync();
        var logg = await _logg.HandlingerSidenAsync(fraUtc);
        var anrop = await _dialer.SidenAsync(fraUtc);
        var sendinger = await _bank.SisteePerKortOgBankAsync();
        var brukere = await AgenterAsync();

        var kortMap = alle.ToDictionary(k => k.Id);
        bool ErKundetype(Kundekort k) => kundetype is null || string.Equals(k.KundeType, kundetype, OIC);
        bool KortOk(Guid? id) => kundetype is null || (id is { } g && kortMap.TryGetValue(g, out var k) && ErKundetype(k));
        bool ErAgentEier(Kundekort k) => agent is null || (k.Eier?.Equals(agent.Email, OIC) ?? false);
        bool ErAgentAktor(string? aktor) => agent is null
            ? ErMenneske(aktor)
            : aktor is not null && (aktor.Equals(agent.Email, OIC) || aktor.Equals(agent.FullName, OIC));
        // Menneskelig aktør = en registrert bruker. System-/kundeaktører («System (Instabank-synk)»,
        // «Kunde (+47…)») regnes ikke som agentaktivitet.
        bool ErMenneske(string? aktor) => !string.IsNullOrWhiteSpace(aktor) && (brukere.Count > 0
            ? brukere.Any(u => aktor.Equals(u.Email, OIC) || aktor.Equals(u.FullName, OIC))
            : !(aktor.StartsWith("System", OIC) || aktor.StartsWith("Kunde", OIC) || aktor.StartsWith("GDPR", OIC)));
        static string Type(Kundekort k) => Produktkategori.Effektiv(k);
        static string Kilde(Kundekort k) => string.IsNullOrWhiteSpace(k.Kilde) ? "Ukjent" : k.Kilde.Trim();
        static decimal Sum(IEnumerable<Kundekort> ks) => ks.Sum(k => k.OnsketLaanebelop ?? 0);
        static decimal? Snitt(IEnumerable<Kundekort> ks)
        {
            var med = ks.Where(k => (k.OnsketLaanebelop ?? 0) > 0).Select(k => k.OnsketLaanebelop!.Value).ToList();
            return med.Count > 0 ? Math.Round(med.Average()) : null;
        }

        var grunnlag = alle.Where(ErKundetype).ToList();

        // ---- Øyeblikksbilde (ikke datostyrt) ----
        var siste30 = grunnlag.Where(k => k.CreatedAt >= DateTime.UtcNow.AddDays(-30) && ErAgentEier(k)).ToList();
        // Samme definisjon som dashbordets pipeline-panel: alt som ikke er utbetalt eller avslått.
        var pipe = grunnlag.Where(k => k.Status is not (KundekortService.StatusUtbetalt or KundekortService.StatusAvslatt) && ErAgentEier(k)).ToList();
        var pipeMed = pipe.Where(k => (k.OnsketLaanebelop ?? 0) > 0).ToList();
        var pipeUten = pipe.Where(k => (k.OnsketLaanebelop ?? 0) <= 0).ToList();
        var snittAlle = pipeMed.Count > 0 ? pipeMed.Average(k => k.OnsketLaanebelop!.Value) : 0m;
        var snittPerType = pipeMed.GroupBy(Type).ToDictionary(g => g.Key, g => g.Average(k => k.OnsketLaanebelop!.Value));
        var estimert = pipeUten.Sum(k => snittPerType.TryGetValue(Type(k), out var s) ? s : snittAlle);
        var oppgitt = Sum(pipeMed);

        // ---- Innkommet i perioden ----
        var inn = grunnlag.Where(k => IPeriode(k.CreatedAt) && ErAgentEier(k)).ToList();

        // ---- Agentaktivitet i perioden ----
        var handlinger = logg.Where(l => IPeriode(l.Opprettet) && ErAgentAktor(l.Aktor) && KortOk(l.KundekortId)).ToList();
        var ring = anrop.Where(a => IPeriode(a.StartetAt) && ErAgentAktor(a.Aktor) && KortOk(a.KundekortId)).ToList();
        var sendt = sendinger.Where(s => IPeriode(s.SendtAt) && KortOk(s.KundekortId)
            && (agent is null || ErAgentAktor(s.SendtAv))).ToList();
        var eierskap = grunnlag.Where(k => k.EierTattAt is { } t && IPeriode(t) && ErAgentEier(k)).ToList();

        // ---- Bankutfall i perioden (på sendingene) ----
        static bool Innvilget(BankSending s) => s.Utfall is SendUtfall.Innvilget or SendUtfall.Utbetalt;
        object BankRad(string bank, List<BankSending> xs) => new
        {
            bank,
            sendinger = xs.Count,
            innvilget_antall = xs.Count(Innvilget),
            innvilget_sum = xs.Where(Innvilget).Sum(s => s.InnvilgetBelop ?? 0),
            utbetalt_antall = xs.Count(s => s.Utfall == SendUtfall.Utbetalt),
            utbetalt_sum = xs.Where(s => s.Utfall == SendUtfall.Utbetalt).Sum(s => s.InnvilgetBelop ?? 0),
            avslatt = xs.Count(s => s.Utfall == SendUtfall.Avslatt),
            kansellert = xs.Count(s => s.Utfall == SendUtfall.Kansellert),
            venter = xs.Count(s => s.Utfall is null or SendUtfall.Venter),
        };

        // ---- Saksutfall i perioden (statusendringer i loggen, uavhengig av hvem som satte dem) ----
        var statusLogg = logg.Where(l => IPeriode(l.Opprettet) && l.Kategori == "endring" && KortOk(l.KundekortId)
                && (agent is null || (kortMap.TryGetValue(l.KundekortId, out var k) && ErAgentEier(k))))
            .Select(l => (l.KundekortId, Status: LoggService.ParseStatus(l.Tekst)))
            .Where(x => x.Status is not null).ToList();
        int Utfall(string status) => statusLogg.Where(x => x.Status!.Equals(status, OIC)).Select(x => x.KundekortId).Distinct().Count();

        // ---- Per agent (kun når ikke filtrert på én agent) ----
        object? perAgent = agent is null ? brukere.Select(u =>
        {
            bool Mine(string? aktor) => aktor is not null && (aktor.Equals(u.Email, OIC) || aktor.Equals(u.FullName, OIC));
            var h = handlinger.Where(l => Mine(l.Aktor)).ToList();
            var r = ring.Where(a => Mine(a.Aktor)).ToList();
            return new
            {
                agent = u.FullName,
                saker_innkommet_eid = inn.Count(k => k.Eier?.Equals(u.Email, OIC) ?? false),
                eierskap_tatt = eierskap.Count(k => k.Eier?.Equals(u.Email, OIC) ?? false),
                saker_endret = h.Select(l => l.KundekortId).Distinct().Count(),
                handlinger = h.Count,
                statusendringer = h.Count(l => l.Kategori == "endring" && LoggService.ParseStatus(l.Tekst) is not null),
                ringeforsok = r.Count,
                ringeforsok_svart = r.Count(a => a.Utfall == DialerService.UtfallSvart),
                taletid_min = Math.Round(r.Sum(a => a.TaletidSek ?? 0) / 60.0, 1),
                sms_sendt = h.Count(l => l.Kategori == "sms"),
                oppgaver_kvittert = h.Count(l => l.Kategori == "arbeidsoppgave"),
                sendt_til_bank = sendt.Count(s => Mine(s.SendtAv)),
                aapne_saker_naa = pipe.Count(k => k.Eier?.Equals(u.Email, OIC) ?? false),
            };
        }).OrderByDescending(x => x.handlinger).ThenBy(x => x.agent).ToList() : null;

        return new
        {
            generert = DateTime.UtcNow,
            periode = new { fra = fra.ToString("yyyy-MM-dd"), til = til.ToString("yyyy-MM-dd"), dager = til.DayNumber - fra.DayNumber + 1 },
            filter = new { agent = agent?.FullName, kundetype },

            naa = new
            {
                saker_siste_30_dager = siste30.Count,
                saker_siste_30_dager_sum = Sum(siste30),
                pipeline = new
                {
                    antall_aktive = pipe.Count,
                    antall_med_belop = pipeMed.Count,
                    antall_uten_belop = pipeUten.Count,
                    oppgitt_sum = oppgitt,
                    estimert_sum = Math.Round(estimert),
                    total = Math.Round(oppgitt + estimert),
                    snitt = pipeMed.Count > 0 ? Math.Round(snittAlle) : (decimal?)null,
                    per_status = pipe.GroupBy(k => k.Status).Select(g => new { status = g.Key, antall = g.Count(), sum = Sum(g) })
                        .OrderByDescending(x => x.antall).ToList(),
                    per_type = pipe.GroupBy(Type).Select(g => new { type = g.Key, antall = g.Count(), sum = Sum(g), snitt = Snitt(g) })
                        .OrderByDescending(x => x.sum).ToList(),
                    per_kundetype = pipe.GroupBy(k => k.KundeType).Select(g => new { kundetype = g.Key, antall = g.Count(), sum = Sum(g) })
                        .OrderBy(x => x.kundetype).ToList(),
                },
            },

            innkommet = new
            {
                antall = inn.Count,
                sum = Sum(inn),
                snitt = Snitt(inn),
                antall_med_belop = inn.Count(k => (k.OnsketLaanebelop ?? 0) > 0),
                per_kilde = inn.GroupBy(Kilde).Select(g => new { kilde = g.Key, antall = g.Count(), sum = Sum(g) })
                    .OrderByDescending(x => x.antall).ToList(),
                per_type = inn.GroupBy(Type).Select(g => new { type = g.Key, antall = g.Count(), sum = Sum(g), snitt = Snitt(g) })
                    .OrderByDescending(x => x.antall).ToList(),
                per_kundetype = inn.GroupBy(k => k.KundeType).Select(g => new { kundetype = g.Key, antall = g.Count(), sum = Sum(g), snitt = Snitt(g) })
                    .OrderBy(x => x.kundetype).ToList(),
                per_kilde_og_type = inn.GroupBy(k => (Kilde: Kilde(k), Type: Type(k)))
                    .Select(g => new { kilde = g.Key.Kilde, type = g.Key.Type, antall = g.Count(), sum = Sum(g) })
                    .OrderByDescending(x => x.antall).ToList(),
                per_dag = inn.GroupBy(k => DateOnly.FromDateTime(k.CreatedAt.TilOslo()))
                    .Select(g => new { dato = g.Key.ToString("yyyy-MM-dd"), antall = g.Count(), sum = Sum(g) })
                    .OrderBy(x => x.dato).ToList(),
                status_naa = inn.GroupBy(k => k.Status).Select(g => new { status = g.Key, antall = g.Count() })
                    .OrderByDescending(x => x.antall).ToList(),
            },

            agentaktivitet = new
            {
                saker_endret = handlinger.Select(l => l.KundekortId).Distinct().Count(),
                handlinger = handlinger.Count,
                statusendringer = handlinger.Count(l => l.Kategori == "endring" && LoggService.ParseStatus(l.Tekst) is not null),
                eierskap_tatt = eierskap.Count,
                sms_sendt = handlinger.Count(l => l.Kategori == "sms"),
                oppgaver_kvittert = handlinger.Count(l => l.Kategori == "arbeidsoppgave"),
                sendt_til_bank = sendt.Count(s => agent is not null || ErMenneske(s.SendtAv)),
                sendt_til_bank_automatisk = agent is null ? sendt.Count(s => !ErMenneske(s.SendtAv)) : 0,
                ringeforsok = new
                {
                    antall = ring.Count,
                    unike_saker = ring.Select(a => a.KundekortId).Distinct().Count(),
                    svart = ring.Count(a => a.Utfall == DialerService.UtfallSvart),
                    ikke_svart = ring.Count(a => a.Utfall == DialerService.UtfallIkkeSvart),
                    uavklart = ring.Count(a => a.Status == DialerService.StatusUavklart),
                    taletid_min = Math.Round(ring.Sum(a => a.TaletidSek ?? 0) / 60.0, 1),
                    per_dag = ring.GroupBy(a => DateOnly.FromDateTime(a.StartetAt.TilOslo()))
                        .Select(g => new { dato = g.Key.ToString("yyyy-MM-dd"), antall = g.Count(), svart = g.Count(a => a.Utfall == DialerService.UtfallSvart) })
                        .OrderBy(x => x.dato).ToList(),
                },
                per_kategori = handlinger.GroupBy(l => l.Kategori).Select(g => new { kategori = g.Key, antall = g.Count() })
                    .OrderByDescending(x => x.antall).ToList(),
                statuser_satt = handlinger.Where(l => l.Kategori == "endring").Select(l => LoggService.ParseStatus(l.Tekst))
                    .Where(s => s is not null).GroupBy(s => s!).Select(g => new { status = g.Key, antall = g.Count() })
                    .OrderByDescending(x => x.antall).ToList(),
                per_dag = handlinger.GroupBy(l => DateOnly.FromDateTime(l.Opprettet.TilOslo()))
                    .Select(g => new { dato = g.Key.ToString("yyyy-MM-dd"), handlinger = g.Count(), saker = g.Select(l => l.KundekortId).Distinct().Count() })
                    .OrderBy(x => x.dato).ToList(),
                per_agent = perAgent,
            },

            bank = new
            {
                sendinger = sendt.Count,
                innvilget_antall = sendt.Count(Innvilget),
                innvilget_sum = sendt.Where(Innvilget).Sum(s => s.InnvilgetBelop ?? 0),
                utbetalt_antall = sendt.Count(s => s.Utfall == SendUtfall.Utbetalt),
                utbetalt_sum = sendt.Where(s => s.Utfall == SendUtfall.Utbetalt).Sum(s => s.InnvilgetBelop ?? 0),
                avslatt = sendt.Count(s => s.Utfall == SendUtfall.Avslatt),
                innvilgelsesgrad_pst = sendt.Count > 0 ? Math.Round(100.0 * sendt.Count(Innvilget) / sendt.Count, 1) : (double?)null,
                per_bank = sendt.GroupBy(s => s.Bank).Select(g => BankRad(g.Key, g.ToList())).ToList(),
            },

            saksutfall = new
            {
                utbetalt = Utfall(KundekortService.StatusUtbetalt),
                innvilget = Utfall(KundekortService.StatusInnvilget),
                avslag = Utfall(KundekortService.StatusAvslatt),
                kansellert = Utfall(KundekortService.StatusKansellert),
                sendt_til_bank = Utfall(KundekortService.StatusSendtIProsess),
            },
        };
    }
}
