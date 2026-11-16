using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

public class ExternalLink
{
    [Key]
    public int Id { get; set; }

    public Guid ItemId { get; set; }

    [ForeignKey(nameof(ItemId))]
    public VaultItem? Item { get; set; }

    /// <summary>Provider id: LastPass, KeePass, Bitwarden, Shared, etc.</summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Origin store id in the provider vault (KeePass UUID, LastPass account id, etc.).
    /// Required to push Shared edits back to the connected source.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(128)]
    public string? ContentHash { get; set; }

    public bool IsMismatch { get; set; }
}
