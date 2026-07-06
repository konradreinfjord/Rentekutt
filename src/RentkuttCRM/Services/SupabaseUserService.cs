using Microsoft.AspNetCore.Identity;
using Supabase.Postgrest;

namespace RentkuttCRM.Services;

public record UserRow(Guid Id, string Email, string FullName, string Role, bool Active, string? Mobilnummer = null, bool TwoFactorEnabled = false);
public record SignInResult(bool Ok, string? Error, UserRow? User);

/// <summary>
/// Innlogging + brukeradministrasjon.
///
/// - Når Supabase er konfigurert (Url + service_role-nøkkel): bruker app_users-tabellen.
/// - Ellers: in-memory staging-fallback, så lokal kjøring uten nøkler fungerer.
///
/// Passord hashes med ASP.NET PasswordHasher. Bytt gjerne til Supabase Auth senere.
/// </summary>
public class SupabaseUserService
{
    public static readonly string[] Roles = { "Saksbehandler", "Compliance", "Leder", "Administrator" };

    private const string DefaultAdminEmail = "admin@rentekutt.no";

    private readonly Supabase.Client _client;
    private readonly ILogger<SupabaseUserService> _log;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public bool IsConfigured { get; }

    // Staging-lager (kun når Supabase ikke er konfigurert). Statisk → overlever per økt.
    private static readonly List<AppUser> _staging = new()
    {
        new() { Id = Guid.NewGuid(), Email = "dev@rentekutt.no", FullName = "Dev Saksbehandler", Role = "Saksbehandler", Active = true },
        new() { Id = Guid.NewGuid(), Email = "anne@rentekutt.no", FullName = "Anne Compliance", Role = "Compliance", Active = true },
        new() { Id = Guid.NewGuid(), Email = "bjorn@rentekutt.no", FullName = "Bjørn Leder", Role = "Leder", Active = true },
        new() { Id = Guid.NewGuid(), Email = "cathrine@rentekutt.no", FullName = "Cathrine Admin", Role = "Administrator", Active = true },
    };

    private static bool _seeded;
    private bool _initialized;

    private readonly SettingsService _settings;
    // Seed-admin passord fra konfig (Admin:SeedPassword / env Admin__SeedPassword).
    // Tomt → genereres tilfeldig og logges én gang ved første oppstart.
    private readonly string? _seedPassword;
    private readonly string _seedEmail;
    // Sporing av feilede innloggingsforsøk per e-post (mot brute-force).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<DateTime>> _failedLogins = new();

    public const string KeyMaxFailHour = "login_max_fail_hour";
    public const string KeyMaxFail24h = "login_max_fail_24h";
    public const int DefaultMaxFailHour = 5;
    public const int DefaultMaxFail24h = 20;

    public SupabaseUserService(Supabase.Client client, IConfiguration cfg, SettingsService settings, ILogger<SupabaseUserService> log)
    {
        _client = client;
        _settings = settings;
        _log = log;
        IsConfigured = !string.IsNullOrWhiteSpace(cfg["Supabase:Url"])
                       && !string.IsNullOrWhiteSpace(cfg["Supabase:Key"]);
        _seedPassword = cfg["Admin:SeedPassword"];
        _seedEmail = cfg["Admin:SeedEmail"] ?? DefaultAdminEmail;
    }

    private static string GenererPassord()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#%+";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
            sb.Append(chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    // ---------- Innlogging ----------
    public async Task<SignInResult> SignInAsync(string email, string password)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            return new SignInResult(false, "Skriv inn e-post.", null);

        if (!IsConfigured)
        {
            // Staging: aksepter alle, finn evt. matchende dummybruker.
            var u = _staging.FirstOrDefault(x => x.Email == email);
            if (u is { Active: false })
                return new SignInResult(false, "Brukeren er deaktivert.", null);
            var name = u?.FullName ?? email.Split('@')[0];
            var role = u?.Role ?? "Administrator";
            return new SignInResult(true, null, new UserRow(u?.Id ?? Guid.Empty, email, name, role, true));
        }

        // Brute-force-sperre: blokker hvis for mange feilforsøk i vinduet.
        var maxHour = await _settings.GetIntAsync(KeyMaxFailHour, DefaultMaxFailHour);
        var max24 = await _settings.GetIntAsync(KeyMaxFail24h, DefaultMaxFail24h);
        if (ErLaast(email, maxHour, max24))
            return new SignInResult(false, "For mange feilforsøk. Prøv igjen senere.", null);

        try
        {
            await EnsureReadyAsync();
            var user = await _client.From<AppUser>().Where(x => x.Email == email).Single();
            if (user is null)
            {
                RegistrerFeil(email);
                return new SignInResult(false, "Feil e-post eller passord.", null);
            }
            if (!user.Active)
                return new SignInResult(false, "Brukeren er deaktivert.", null);

            var verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (verify == PasswordVerificationResult.Failed)
            {
                RegistrerFeil(email);
                return new SignInResult(false, "Feil e-post eller passord.", null);
            }

            NullstillFeil(email);
            return new SignInResult(true, null, ToRow(user));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Innlogging mot Supabase feilet");
            return new SignInResult(false, "Teknisk feil ved innlogging. Prøv igjen.", null);
        }
    }

    private static bool ErLaast(string email, int maxHour, int max24)
    {
        if (!_failedLogins.TryGetValue(email, out var l)) return false;
        lock (l)
        {
            var na = DateTime.UtcNow;
            l.RemoveAll(t => t < na.AddHours(-24));
            return l.Count(t => t >= na.AddHours(-1)) >= maxHour || l.Count >= max24;
        }
    }

    private static void RegistrerFeil(string email)
    {
        var l = _failedLogins.GetOrAdd(email, _ => new List<DateTime>());
        lock (l) l.Add(DateTime.UtcNow);
    }

    private static void NullstillFeil(string email) => _failedLogins.TryRemove(email, out _);

    // ---------- Brukeradministrasjon ----------
    public async Task<List<UserRow>> ListUsersAsync()
    {
        if (!IsConfigured)
            return _staging.Select(ToRow).ToList();

        try
        {
            await EnsureReadyAsync();
            var res = await _client.From<AppUser>().Order(x => x.CreatedAt, Constants.Ordering.Ascending, Constants.NullPosition.Last).Get();
            return res.Models.Select(ToRow).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Henting av brukere feilet");
            return new List<UserRow>();
        }
    }

    public async Task<(bool ok, string? error)> CreateUserAsync(string email, string fullName, string role, string password, string? mobil = null)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return (false, "E-post og passord er påkrevd.");
        if (!Roles.Contains(role)) role = "Saksbehandler";

        if (!IsConfigured)
        {
            if (_staging.Any(x => x.Email == email)) return (false, "E-posten finnes allerede.");
            _staging.Add(new AppUser { Id = Guid.NewGuid(), Email = email, FullName = fullName, Role = role, Active = true, Mobilnummer = mobil });
            return (true, null);
        }

        try
        {
            await EnsureReadyAsync();
            var existing = await _client.From<AppUser>().Where(x => x.Email == email).Single();
            if (existing is not null) return (false, "E-posten finnes allerede.");

            var user = new AppUser { Email = email, FullName = fullName, Role = role, Active = true, Mobilnummer = mobil };
            user.PasswordHash = _hasher.HashPassword(user, password);
            await _client.From<AppUser>().Insert(user);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Oppretting av bruker feilet");
            return (false, "Teknisk feil ved oppretting.");
        }
    }

    public async Task<(bool ok, string? error)> SetPasswordAsync(Guid id, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            return (false, "Passord må være minst 6 tegn.");
        if (!IsConfigured) return (true, null); // staging godtar uansett alle passord
        try
        {
            await EnsureReadyAsync();
            var hash = _hasher.HashPassword(new AppUser { Id = id }, newPassword);
            await _client.From<AppUser>().Where(x => x.Id == id).Set(x => x.PasswordHash, hash).Update();
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Endring av passord feilet");
            return (false, "Teknisk feil ved endring av passord.");
        }
    }

    public async Task SetRoleAsync(Guid id, string role)
    {
        if (!Roles.Contains(role)) return;
        if (!IsConfigured)
        {
            var u = _staging.FirstOrDefault(x => x.Id == id);
            if (u is not null) u.Role = role;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<AppUser>().Where(x => x.Id == id).Set(x => x.Role, role).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av rolle feilet"); }
    }

    public async Task SetActiveAsync(Guid id, bool active)
    {
        if (!IsConfigured)
        {
            var u = _staging.FirstOrDefault(x => x.Id == id);
            if (u is not null) u.Active = active;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<AppUser>().Where(x => x.Id == id).Set(x => x.Active, active).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av status feilet"); }
    }

    /// <summary>Init + seeding av standard-admin (kalles ved oppstart).</summary>
    public async Task EnsureSeededPublicAsync()
    {
        if (!IsConfigured) return;
        await EnsureReadyAsync();
    }

    // ---------- Internt ----------
    private async Task EnsureReadyAsync()
    {
        if (!_initialized)
        {
            try { await _client.InitializeAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Supabase InitializeAsync ga feil (fortsetter)"); }
            _initialized = true;
        }
        await EnsureSeededAsync();
    }

    private async Task EnsureSeededAsync()
    {
        if (_seeded) return;
        _seeded = true; // sett tidlig for å unngå dobbel-seeding ved samtidige kall
        try
        {
            var any = (await _client.From<AppUser>().Limit(1).Get()).Models.Count > 0;
            if (!any)
            {
                // Aldri generere+logge et passord i klartekst (havner i Azure-logg/App Insights).
                // Krev at Admin:SeedPassword settes eksplisitt; ellers hopp over seeding og prøv igjen senere.
                if (string.IsNullOrWhiteSpace(_seedPassword))
                {
                    _log.LogWarning("Hopper over admin-seeding: sett App Setting «Admin:SeedPassword» og start på nytt for å opprette {Email}. Bytt passordet etter første innlogging.", _seedEmail);
                    _seeded = false; // tillat seeding når passordet er konfigurert
                    return;
                }
                var admin = new AppUser
                {
                    Email = _seedEmail,
                    FullName = "Administrator",
                    Role = "Administrator",
                    Active = true,
                };
                admin.PasswordHash = _hasher.HashPassword(admin, _seedPassword);
                await _client.From<AppUser>().Insert(admin);
                _log.LogInformation("Opprettet admin {Email} med passord fra konfigurasjon (Admin:SeedPassword). Bytt det etter første innlogging.", _seedEmail);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Seeding av admin-bruker feilet");
            _seeded = false; // tillat nytt forsøk senere
        }
    }

    private static UserRow ToRow(AppUser u) => new(u.Id, u.Email, u.FullName, u.Role, u.Active, u.Mobilnummer, u.TwoFactorEnabled);

    public async Task SetMobilAsync(Guid id, string? mobil)
    {
        mobil = string.IsNullOrWhiteSpace(mobil) ? null : mobil.Trim();
        if (!IsConfigured)
        {
            var u = _staging.FirstOrDefault(x => x.Id == id);
            if (u is not null) u.Mobilnummer = mobil;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<AppUser>().Where(x => x.Id == id).Set(x => x.Mobilnummer!, mobil!).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av mobilnummer feilet"); }
    }

    public async Task SetTwoFactorAsync(Guid id, bool enabled)
    {
        if (!IsConfigured)
        {
            var u = _staging.FirstOrDefault(x => x.Id == id);
            if (u is not null) u.TwoFactorEnabled = enabled;
            return;
        }
        try
        {
            await EnsureReadyAsync();
            await _client.From<AppUser>().Where(x => x.Id == id).Set(x => x.TwoFactorEnabled, enabled).Update();
        }
        catch (Exception ex) { _log.LogError(ex, "Endring av 2FA feilet"); }
    }
}
