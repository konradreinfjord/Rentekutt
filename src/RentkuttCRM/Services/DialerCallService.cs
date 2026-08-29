namespace RentkuttCRM.Services;

/// <summary>
/// Holder tilstanden til det pågående anropet for den innloggede agenten (én om gangen), slik at
/// den globale ringebaren i headeren kan vise status/varighet og overleve fanebytte. Scoped =
/// lever så lenge Blazor-circuiten (agentens økt) lever.
///
/// Zisson har ingen sanntids samtalestatus eller «legg på»-endepunkt, så:
///  • Varighet telles klient-side fra oppringingstidspunktet.
///  • Tilstand oppdateres ved å spørre CDR (ConversationSessions) hvert par sekunder.
///  • «Kutt» gjøres ved å logge agenten av i Zisson (dropper samtalen).
/// </summary>
public class DialerCallService : IDisposable
{
    public enum Tilstand { Ringer, Besvart, Avsluttet, Feil }

    public class Anrop
    {
        public string? Zid { get; set; }
        public string Nummer { get; set; } = "";
        public string? Agent { get; set; }
        public DateTime StartUtc { get; set; }
        public Tilstand Tilstand { get; set; } = Tilstand.Ringer;
        public int? TaletidSek { get; set; }
        public Guid? KundekortId { get; set; }
        public bool Medsoker { get; set; }
        public string? Melding { get; set; }
        /// <summary>Sekunder siden oppringing (klient-side teller).</summary>
        public int GaattSek => Math.Max(0, (int)(DateTime.UtcNow - StartUtc).TotalSeconds);
    }

    public Anrop? Aktiv { get; private set; }
    public event Action? OnChange;

    /// <summary>Er den innloggede agenten pålogget Zisson? null = ikke sjekket ennå.</summary>
    public bool? Paalogget { get; private set; }
    public string AgentKlientUrl => _zisson.AgentKlientUrl;

    /// <summary>Sjekker (og oppdaterer) om agenten er pålogget Zisson.</summary>
    public async Task SjekkPaaloggingAsync()
    {
        var agent = await _users.ZissonAgentGuidAsync(_session.UserId);
        Paalogget = !string.IsNullOrWhiteSpace(agent) && await _zisson.ErAgentPaaloggetAsync(agent!);
        Varsle();
    }

    private readonly ZissonService _zisson;
    private readonly DialerService _dialer;
    private readonly LoggService _logg;
    private readonly SupabaseUserService _users;
    private readonly SessionState _session;
    private readonly ILogger<DialerCallService> _log;

    private System.Threading.Timer? _timer;
    private int _tick;

    public DialerCallService(ZissonService zisson, DialerService dialer, LoggService logg,
        SupabaseUserService users, SessionState session, ILogger<DialerCallService> log)
    {
        _zisson = zisson;
        _dialer = dialer;
        _logg = logg;
        _users = users;
        _session = session;
        _log = log;
    }

    /// <summary>Starter et anrop for den innloggede agenten. Er kundekortId satt, logges anropet på
    /// søknaden. Returnerer resultatet fra Zisson (for umiddelbar tilbakemelding til den som ringer).</summary>
    public async Task<ZissonService.RingeResultat> RingAsync(string nummer, Guid? kundekortId = null, bool medsoker = false)
    {
        var agent = await _users.ZissonAgentGuidAsync(_session.UserId);
        if (string.IsNullOrWhiteSpace(agent))
            return new(false, -1, "Du er ikke koblet til en Zisson-agent (Admin → Dialer → Agent-kobling).", null);

        // Oppdater pålogging-statusen (rådgivende) – men BLOKKER IKKE på den. Statdb kan gi falsk
        // «ikke pålogget» (login-guid ≠ bruker-guid / forsinkelse). Den autoritative kilden er Zissons
        // egen responseCode 3 («opptatt/ikke pålogget/klar»), som ClickToCall tolker.
        Paalogget = await _zisson.ErAgentPaaloggetAsync(agent!);
        Varsle();

        var r = await _zisson.ClickToCallAsync(agent, nummer);
        if (!r.Ok)
        {
            Aktiv = new Anrop { Nummer = nummer, Agent = agent, StartUtc = DateTime.UtcNow, Tilstand = Tilstand.Feil, Melding = r.Melding, KundekortId = kundekortId, Medsoker = medsoker };
            Varsle();
            return r;
        }

        Aktiv = new Anrop
        {
            Zid = r.Zid, Nummer = nummer, Agent = agent, StartUtc = DateTime.UtcNow, Tilstand = Tilstand.Ringer,
            KundekortId = kundekortId, Medsoker = medsoker,
            // Diagnostikk i baren: bekrefter at Zisson opprettet en økt (zid) og hvilken agent som ringes.
            Melding = string.IsNullOrWhiteSpace(r.Zid) ? "Zisson svarte OK, men uten zid" : $"zid {r.Zid} · agent {agent}",
        };

        if (kundekortId is Guid kid)
        {
            var aktor = _session.UserName ?? _session.Email;
            var norm = ZissonService.NormaliserNummer(nummer);
            await _dialer.OpprettAsync(kid, aktor, agent, norm, r.Zid);
            await _logg.LoggAsync(kid, aktor, $"📞 Utgående anrop startet til {(medsoker ? "medsøker" : "kunde")} ({MaskerNr(nummer)})", "anrop");
        }

        StartTimer();
        Varsle();
        return r;
    }

    /// <summary>Avbryter anropet ved å logge agenten av i Zisson (eneste tilgjengelige mekanisme).</summary>
    public async Task KuttAsync()
    {
        if (Aktiv is null) return;
        var ok = !string.IsNullOrWhiteSpace(Aktiv.Agent) && await _zisson.LoggAvAgentAsync(Aktiv.Agent!);
        Aktiv.Tilstand = Tilstand.Avsluttet;
        Aktiv.Melding = ok ? "Kuttet – agent logget av i Zisson." : "Forsøkte å kutte, men log-off feilet.";
        StoppTimer();
        Varsle();
    }

    /// <summary>Lukker/fjerner baren (etter avsluttet/feilet anrop).</summary>
    public void Lukk()
    {
        Aktiv = null;
        StoppTimer();
        Varsle();
    }

    private void StartTimer()
    {
        _tick = 0;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(async _ => await Tick(), null, 1000, 1000);
    }

    private void StoppTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task Tick()
    {
        if (Aktiv is null) return;
        _tick++;
        // Poll CDR hvert 4. sekund så lenge samtalen ikke er avsluttet.
        if (Aktiv.Tilstand is Tilstand.Ringer or Tilstand.Besvart && _tick % 4 == 0)
        {
            try
            {
                // Korreler på det oppringte nummeret (kunde-benet), ikke zid.
                var u = await _zisson.HentUtfallAsync(Aktiv.Nummer, Aktiv.StartUtc.AddMinutes(-2), DateTime.UtcNow.AddMinutes(1));
                if (u.Funnet)
                {
                    if (u.Avsluttet && (u.TaletidSek > 0 || Aktiv.GaattSek > 25))
                    {
                        // Ekte avslutning: enten med taletid, eller etter at oppsett-vinduet er passert.
                        Aktiv.Tilstand = Tilstand.Avsluttet;
                        Aktiv.TaletidSek = u.TaletidSek;
                        StoppTimer();
                    }
                    else if (!u.Avsluttet && Aktiv.Tilstand == Tilstand.Ringer)
                    {
                        Aktiv.Tilstand = Tilstand.Besvart;
                    }
                    // (avsluttet + 0 taletid de første ~25 s = oppsett-benet i CDR → behold «Ringer opp…»)
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Dialer live-status-oppslag feilet"); }
        }
        Varsle();
    }

    private void Varsle() => OnChange?.Invoke();

    // Viser kun siste 4 sifre i loggen (personvern).
    private static string MaskerNr(string? n)
    {
        var d = new string((n ?? "").Where(char.IsDigit).ToArray());
        return d.Length >= 4 ? "•••• " + d[^4..] : (n ?? "");
    }

    public void Dispose() => _timer?.Dispose();
}
