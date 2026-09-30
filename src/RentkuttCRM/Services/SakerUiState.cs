namespace RentkuttCRM.Services;

/// <summary>
/// Holder valgt fane/filter på Saker og Marked så de overlever at agenten går inn på en
/// søknad og trykker tilbake. Scoped = lever så lenge Blazor-circuiten (samme fane/økt) lever.
/// </summary>
public class SakerUiState
{
    // ---- Saker (/crm/oppfolging) ----
    public string Tab = "alle";
    public string DelegertFilter = "";
    public bool KunOppgaver;
    public string AlleType = "";
    public string NyeType = "";
    public string OppfType = "";
    public string TypeFilter = "";
    public string SendtFilter = "";
    public string LaanetypeFilter = "";
    public string StatusFilter = "";
    public bool VisStatus;
    public string OppfFilter = "";
    public DateTime? OppfFra;
    public DateTime? OppfTil;
    public string SelectedEier = "__mine__";

    // ---- Database (/crm/database) ----
    public DatabaseFilter Database { get; } = new();

    // ---- Marked (/crm/marked/{marked}) — per marked (person/bedrift) ----
    private readonly Dictionary<string, MarkedFilter> _marked = new();
    public MarkedFilter Marked(string key)
    {
        if (!_marked.TryGetValue(key, out var f)) { f = new MarkedFilter(); _marked[key] = f; }
        return f;
    }
}

public class DatabaseFilter
{
    public string Search = "";
    public string StatusFilter = "";
    public string TypeFilter = "";
    public string BankFilter = "";
    public string DelegertFilter = "";
    public string KommuneFilter = "";
    public string SortField = "opprettet";
    public bool SortAsc;
}

public class MarkedFilter
{
    public string Search = "";
    public string StatusFilter = "";
    public string LaanetypeFilter = "";
    public string KildeFilter = "";
    public string DelegertFilter = "";
    public string SortField = "opprettet";
    public bool SortAsc;
}
