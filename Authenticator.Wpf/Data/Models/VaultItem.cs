using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

public class VaultItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public VaultItemType ItemType { get; set; } = VaultItemType.Login;

    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    public int? FolderId { get; set; }

    [ForeignKey(nameof(FolderId))]
    public VaultFolder? Folder { get; set; }

    /// <summary>Encrypted when sensitive.</summary>
    public string? Notes { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsDeleted { get; set; }

    [MaxLength(300)]
    public string? Username { get; set; }

    /// <summary>Encrypted password / secret.</summary>
    public string? Password { get; set; }

    [MaxLength(1000)]
    public string? Url { get; set; }

    public int? LinkedAccountId { get; set; }

    [ForeignKey(nameof(LinkedAccountId))]
    public Account? LinkedAccount { get; set; }

    /// <summary>Optional inline TOTP secret (encrypted), e.g. LastPass import.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Encrypted JSON payload for type-specific fields.</summary>
    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }

    /// <summary>Set when the user copies credentials or auto-fills — drives Recents.</summary>
    public DateTime? LastUsedAt { get; set; }

    public virtual ICollection<VaultCustomField> CustomFields { get; set; } = new List<VaultCustomField>();

    public virtual ICollection<ExternalLink> ExternalLinks { get; set; } = new List<ExternalLink>();

    /// <summary>UI-only: mismatch with any linked provider.</summary>
    [NotMapped]
    public bool HasMismatch { get; set; }
}
