using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

/// <summary>
/// Represents a 2FA account with TOTP configuration
/// </summary>
public class Account
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? ServiceUrl { get; set; }

    [Required]
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Base32-encoded secret key for TOTP generation
    /// </summary>
    [Required]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Hash algorithm: SHA1, SHA256, SHA512
    /// </summary>
    [MaxLength(10)]
    public string Algorithm { get; set; } = "SHA1";

    /// <summary>
    /// Number of digits in generated code (6 or 8)
    /// </summary>
    public int Digits { get; set; } = 6;

    /// <summary>
    /// Time period in seconds (15, 30, 60, 120)
    /// </summary>
    public int Period { get; set; } = 30;

    /// <summary>
    /// Optional folder ID for organization
    /// </summary>
    public int? FolderId { get; set; }

    [ForeignKey(nameof(FolderId))]
    public Folder? Folder { get; set; }

    /// <summary>
    /// Is this account marked as favorite
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Soft delete flag
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Navigation property for backup codes
    /// </summary>
    public virtual ICollection<BackupCode> BackupCodes { get; set; } = new List<BackupCode>();
}
