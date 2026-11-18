using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

/// <summary>
/// Snapshot of a vault item before a Diff Apply — enables undo / recovery.
/// </summary>
public class ReconcileHistoryEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ItemId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Summary { get; set; }

    /// <summary>Sealed JSON snapshot of vault fields + link mismatch state before Apply.</summary>
    [Required]
    public string BeforeJson { get; set; } = "";

    /// <summary>JSON map of field → source id used for Apply.</summary>
    public string? FieldSourcesJson { get; set; }

    public bool PushedToProviders { get; set; }

    [MaxLength(500)]
    public string? ProviderIds { get; set; }

    public DateTime? UndoneAtUtc { get; set; }

    /// <summary>UI-only title lookup for global history.</summary>
    [NotMapped]
    public string? ItemTitle { get; set; }
}
