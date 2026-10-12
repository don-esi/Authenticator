using System;
using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>
/// Represents a password entry from LastPass or Keypass
/// </summary>
public class PasswordEntry
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Source: LastPass, Keypass, etc.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? ServiceUrl { get; set; }

    [Required]
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted password (will implement encryption in service layer)
    /// </summary>
    [Required]
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional link to associated 2FA account
    /// </summary>
    public int? LinkedAccountId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }
}
