namespace RentkuttCRM.Services;

/// <summary>
/// Holder tilstanden til det pågående anropet for den innloggede agenten (én om gangen), slik at
/// den globale ringebaren i headeren kan vise en enkel teller og overleve fanebytte. Scoped =
/// lever så lenge Blazor-circuiten (agentens økt) lever.
///
/// Bevisst enkel: Zisson gir ingen pålitelig sanntids-status eller korrelasjon fra click-to-call
/// til en samtale (zid ≠ conversationId, CDR henger). Derfor viser baren kun «anrop pågår» med en
/// klient-teller til agenten selv lukker/kutter. «Kutt» = log-off i Zisson (bekreftet).
/// </summary>
public class DialerCallService : IDisposable
{
    public enum Tilstand { Pagaar, Avsluttet, Feil }

    public class Anrop
    {
        public string? Zid { get; set; }
        public string Nummer { get; set; } = "";
        public string? Agent { get; set; }
        public DateTime StartUtc { get; set; }
        public Tilstand Tilstand { get; set; } = Tilstand.Pagaar;
        public Guid? KundekortId { get; set; }
        public bool Medsoker { get; set; }
        public string? Melding { get; set; }
        /// <summary>Sekunder siden oppringing (klient-side teller).</summary>
        public int GaattSek => Math.Max(0, (int)(DateTime.UtcNow - StartUtc).TotalSeconds);
    }

    public Anrop? Aktiv { get; private set; }
    public event Action? OnChange;

    private readonly ZissonService _zisson;
    private readonly DialerService _dialer;
    private readonly LoggService _logg;
    private readonly SupabaseUserService _users;
    private readonly SessionState _session;
    private readonly ILogger<DialerCallService> _log;

    private System.Threading.Timer? _timer;

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

        var r = await _zisson.ClickToCallAsync(agent, nummer);
        if (!r.Ok)
        {
            Aktiv = new Anrop { Nummer = nummer, Agent = agent, StartUtc = DateTime.UtcNow, Tilstand = Tilstand.Feil, Melding = r.Melding, KundekortId = kundekortId, Medsoker = medsoker };
            Varsle();
            return r;
        }

        Aktiv = new Anrop { Zid = r.Zid, Nummer = nummer, Agent = agent, StartUtc = DateTime.UtcNow, Tilstand = Tilstand.Pagaar, KundekortId = kundekortId, Medsoker = medsoker };

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

    /// <summary>Avbryter anropet ved å logge agenten av i Zisson (eneste tilgjengelige mekanisme —
    /// nullstiller også en fastlåst agent-tilstand).</summary>
    public async Task KuttAsync()
    {
        if (Aktiv is null) return;
        var ok = !string.IsNullOrWhiteSpace(Aktiv.Agent) && await _zisson.LoggAvAgentAsync(Aktiv.Agent!);
        Aktiv.Tilstand = Tilstand.Avsluttet;
        Aktiv.Melding = ok ? "Kuttet – agent logget av i Zisson." : "Forsøkte å kutte, men log-off feilet.";
        StoppTimer();
        Varsle();
    }

    /// <summary>Lukker/fjerner baren.</summary>
    public void Lukk()
    {
        Aktiv = null;
        StoppTimer();
        Varsle();
    }

    // Enkelt 1-sekunds hjerteslag som bare oppdaterer teller-visningen (ingen CDR-kall).
    private void StartTimer()
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Varsle(), null, 1000, 1000);
    }

    private void StoppTimer()
    {
        _timer?.Dispose();
        _timer = null;
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
