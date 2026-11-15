using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

public class VaultFolder
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Color { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<VaultItem> Items { get; set; } = new List<VaultItem>();
}
