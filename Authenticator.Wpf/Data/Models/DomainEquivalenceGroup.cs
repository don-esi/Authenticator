using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>
/// Global set of hosts treated as the same site for vault matching (e.g. x.ai / grok.com).
/// </summary>
public class DomainEquivalenceGroup
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON array of normalized hosts.</summary>
    [Required]
    public string DomainsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }
}
