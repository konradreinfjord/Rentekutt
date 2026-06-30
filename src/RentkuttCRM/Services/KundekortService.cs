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

    /// <param name="strict">Når true (manuelt skjema) kreves korrekt fødselsnr/orgnr-lengde.
    /// Når false (API/webhook) opprettes saken uansett — id = fødselsnr → mobil → fallback.</param>
    public async Task<(bool ok, string? error)> SaveAsync(Kundekort k, bool strict = false)
    {
        k.KundeId = (k.KundeId ?? "").Trim();

        if (strict)
        {
            var expected = k.KundeType == "B2B" ? 9 : 11;
            if (k.KundeId.Length != expected)
                return (false, $"{(k.KundeType == "B2B" ? "Organisasjonsnummer" : "Fødselsnummer")} må være {expected} siffer.");
        }
        else if (string.IsNullOrWhiteSpace(k.KundeId))
        {
            // Verken fødselsnr eller mobil — opprett saken med en generert id.
            k.KundeId = "lead-" + Guid.NewGuid().ToString("N")[..12];
        }

        if (k.KundeType == "B2C" && string.IsNullOrWhiteSpace(k.Foedselsnummer) && k.KundeId.Length == 11)
            k.Foedselsnummer = k.KundeId;

        if (!IsConfigured)
        {
            if (k.Id == Guid.Empty) k.Id = Guid.NewGuid();
            _staging.RemoveAll(x => x.Id == k.Id);
            _staging.Add(k);
            return (true, null);
        }

        try
        {
            await EnsureReadyAsync();
            // Tom Id = ny sak (DB genererer id). Ellers oppdater eksisterende sak.
            if (k.Id == Guid.Empty)
                await _client.From<Kundekort>().Insert(k);
            else
                await _client.From<Kundekort>().Update(k);
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

    /// <summary>Ta eierskap til en sak (på sak-Id).</summary>
    public async Task SetEierAsync(Guid id, string eier, string eierNavn)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) { k.Eier = eier; k.EierNavn = eierNavn; }
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>()
                .Where(x => x.Id == id)
                .Set(x => x.Eier!, eier)
                .Set(x => x.EierNavn!, eierNavn)
                .Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Sette eier feilet"); }
    }

    public async Task SetStatusAsync(Guid id, string status)
    {
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.Status = status;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.Status, status).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av status feilet"); }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!IsConfigured) { _staging.RemoveAll(x => x.Id == id); return; }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Delete();
        }
        catch (Exception ex) { _log.LogError(ex, "Sletting av kundekort feilet"); }
    }

    public async Task SetDelegertBankAsync(Guid id, string? bank)
    {
        bank = string.IsNullOrWhiteSpace(bank) ? null : bank;
        if (!IsConfigured)
        {
            var k = _staging.FirstOrDefault(x => x.Id == id);
            if (k is not null) k.DelegertBank = bank;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<Kundekort>().Where(x => x.Id == id).Set(x => x.DelegertBank!, bank ?? "").Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Delegering til bank feilet"); }
    }

    public async Task<Kundekort?> GetAsync(Guid id)
    {
        if (!IsConfigured) return _staging.FirstOrDefault(x => x.Id == id);
        try
        {
            await EnsureReadyAsync();
            return await _client.From<Kundekort>().Where(x => x.Id == id).Single();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av kundekort {Id} feilet", id);
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
