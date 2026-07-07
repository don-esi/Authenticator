using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

/// <summary>
/// Represents a backup/recovery code for a 2FA account
/// </summary>
public class BackupCode
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int AccountId { get; set; }

    [ForeignKey(nameof(AccountId))]
    public Account? Account { get; set; }

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Has this code been used/consumed
    /// </summary>
    public bool IsUsed { get; set; }

    /// <summary>
    /// Soft delete flag
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UsedAt { get; set; }
}
