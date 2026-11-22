using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>
/// Provider origin ids dropped during vault-row consolidate (same-provider collision).
/// Sync must not re-import these as new vault rows.
/// </summary>
public class SuppressedExternalOrigin
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = "";

    /// <summary>Normalized external id (trim, strip dashes, lowercase) for stable lookup.</summary>
    [Required]
    [MaxLength(200)]
    public string ExternalIdNormalized { get; set; } = "";

    /// <summary>Original external id as stored on the donor link (for diagnostics).</summary>
    [MaxLength(200)]
    public string? ExternalIdRaw { get; set; }

    public Guid MergedIntoItemId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
