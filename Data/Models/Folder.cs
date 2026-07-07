using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>
/// Represents a folder for organizing accounts
/// </summary>
public class Folder
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Hex color code for folder display (e.g., #4ECDC4 for teal)
    /// </summary>
    [MaxLength(7)]
    public string Color { get; set; } = "#4ECDC4";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property for accounts in this folder
    /// </summary>
    public virtual ICollection<Account> Accounts { get; set; } = new List<Account>();
}
