using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RentkuttCRM.Services;

/// <summary>
/// Innloggings-tilstand per Blazor-økt (in-memory). Settes etter validert innlogging.
/// Byttes ut med cookie/Supabase Auth-sesjon før konsesjon.
/// </summary>
public class SessionState
{
    public SessionState(IHostEnvironment env, IConfiguration config)
    {
        // Auto-login KUN lokalt: krever både Development OG det eksplisitte flagget
        // DevAutoLogin (satt i launchSettings, som ikke deployes). Dermed kan auto-login
        // ALDRI slå inn i produksjon — selv om miljøet ved en feil settes til Development.
        if (env.IsDevelopment() && config.GetValue<bool>("DevAutoLogin"))
            SignIn(new UserRow(Guid.Empty, "dev@rentekutt.no", "Lokal Utvikler", "Administrator", true));
    }

    public bool IsLoggedIn { get; private set; }
    public Guid UserId { get; private set; }
    public string? Email { get; private set; }
    public string? UserName { get; private set; }
    public string Role { get; private set; } = "Saksbehandler";

    public bool IsAdmin => Role is "Administrator";

    public void SignIn(UserRow user)
    {
        IsLoggedIn = true;
        UserId = user.Id;
        Email = user.Email;
        UserName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
        Role = user.Role;
    }

    public void SignOut()
    {
        IsLoggedIn = false;
        UserId = Guid.Empty;
        Email = null;
        UserName = null;
        Role = "Saksbehandler";
    }
}
