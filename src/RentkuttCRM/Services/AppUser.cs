using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

/// <summary>
/// Bruker i app_users-tabellen (Supabase/Postgres). Mappes av Postgrest.
/// </summary>
[Table("app_users")]
public class AppUser : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("email")]
    public string Email { get; set; } = "";

    [Column("full_name")]
    public string FullName { get; set; } = "";

    [Column("role")]
    public string Role { get; set; } = "Saksbehandler";

    [Column("active")]
    public bool Active { get; set; } = true;

    [Column("mobilnummer")]
    public string? Mobilnummer { get; set; }

    [Column("twofa_enabled")]
    public bool TwoFactorEnabled { get; set; }

    [Column("password_hash")]
    public string PasswordHash { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
