namespace RentkuttCRM.Services;

/// <summary>
/// Lagring og henting av kundekort (lånesøknader) mot Supabase.
/// Staging-fallback (in-memory) når Supabase ikke er konfigurert.
/// </summary>
public class KundekortService
{
    public static readonly string[] Laanetyper = { "Forbrukslån", "Refinansiering", "Boliglån" };
    public static readonly string[] Sivilstatuser = { "Singel", "Samboer", "Gift", "Skilt", "Separert", "Enke(mann)" };
    public static readonly string[] Boforhold = { "Selveier/enebolig", "Andel/borettslag", "Leier", "Hos foreldre" };
    public static readonly string[] Arbeidssituasjoner =
        { "Fast ansatt", "Selvstendig næringsdrivende", "Offentlig sektor", "Pensjonist", "Arbeidsledig", "Uføretrygdet", "Hjemmeværende", "Student" };

    private readonly Supabase.Client _client;
    private readonly ILogger<KundekortService> _log;
    public bool IsConfigured { get; }

    private static readonly List<Kundekort> _staging = new();
    private bool _initialized;

    public KundekortService(Supabase.Client client, IConfiguration cfg, ILogger<KundekortService> log)
    {
        _client = client;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
    }

    public async Task<(bool ok, string? error)> SaveAsync(Kundekort k)
    {
        // B2C: id = fødselsnummer. B2B: id = orgnr.
        k.KundeId = (k.KundeId ?? "").Trim();
        var expected = k.KundeType == "B2B" ? 9 : 11;
        if (k.KundeId.Length != expected)
            return (false, $"{(k.KundeType == "B2B" ? "Organisasjonsnummer" : "Fødselsnummer")} må være {expected} siffer.");
        if (k.KundeType == "B2C") k.Foedselsnummer = k.KundeId;

        if (!IsConfigured)
        {
            _staging.RemoveAll(x => x.KundeId == k.KundeId);
            _staging.Add(k);
            return (true, null);
        }

        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Upsert(k);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lagring av kundekort feilet");
            return (false, "Teknisk feil ved lagring.");
        }
    }

    public async Task<List<Kundekort>> ListAsync()
    {
        if (!IsConfigured) return _staging.ToList();
        try
        {
            await EnsureReadyAsync();
            return (await _client.From<Kundekort>().Get()).Models;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort feilet");
            return new List<Kundekort>();
        }
    }

    /// <summary>Saker eid av en bruker (Mine oppfølginger).</summary>
    public async Task<List<Kundekort>> ListByEierAsync(string eier)
    {
        if (string.IsNullOrWhiteSpace(eier)) return new();
        if (!IsConfigured) return _staging.Where(k => k.Eier == eier).ToList();
        try
        {
            await EnsureReadyAsync();
            return (await _client.From<Kundekort>().Where(k => k.Eier == eier).Get()).Models;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av egne saker feilet");
            return new();
        }
    }

    /// <summary>Ta eierskap til en sak.</summary>
    public async Task SetEierAsync(string kundeId, string eier, string eierNavn)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.KundeId == kundeId);
            if (k is not null) { k.Eier = eier; k.EierNavn = eierNavn; }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>()
                .Where(x => x.KundeId == kundeId)
                .Set(x => x.Eier!, eier)
                .Set(x => x.EierNavn!, eierNavn)
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Sette eier feilet"); }
    }

    public async Task<Kundekort?> GetAsync(string kundeId)
    {
        if (!IsConfigured) return _staging.FirstOrDefault(x => x.KundeId == kundeId);
        try
        {
            await EnsureReadyAsync();
            return await _client.From<Kundekort>().Where(x => x.KundeId == kundeId).Single();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort {Id} feilet", kundeId);
            return null;
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_initialized) return;
        try { await _client.InitializeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
        _initialized = true;
    }
}
