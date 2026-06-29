namespace RentkuttCRM.Services;

/// <summary>
/// Enkel innloggings-tilstand for staging/demo. Holder kun i minnet per Blazor-økt.
/// Byttes ut med ekte auth (MFA/Supabase) før konsesjon.
/// </summary>
public class SessionState
{
    public bool IsLoggedIn { get; private set; }
    public string? UserName { get; private set; }
    public string Role { get; private set; } = "Saksbehandler";

    public void SignIn(string userName, string role = "Saksbehandler")
    {
        IsLoggedIn = true;
        UserName = userName;
        Role = role;
    }

    public void SignOut()
    {
        IsLoggedIn = false;
        UserName = null;
    }
}
