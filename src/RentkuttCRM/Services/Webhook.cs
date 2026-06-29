using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace RentkuttCRM.Services;

[Table("webhooks")]
public class Webhook : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";
    [Column("direction")] public string Direction { get; set; } = "inbound";
    [Column("token")] public string Token { get; set; } = "";
    [Column("active")] public bool Active { get; set; } = true;
}
