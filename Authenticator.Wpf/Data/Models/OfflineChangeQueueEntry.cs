using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

public class OfflineChangeQueueEntry
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string EntityType { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string EntityId { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? PayloadHash { get; set; }

    public string? PayloadJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool Applied { get; set; }
}
