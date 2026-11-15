using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Authenticator.Data.Models;

public class VaultCustomField
{
    [Key]
    public int Id { get; set; }

    public Guid ItemId { get; set; }

    [ForeignKey(nameof(ItemId))]
    public VaultItem? Item { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Encrypted when IsSecret.</summary>
    public string? Value { get; set; }

    public bool IsSecret { get; set; }

    public int SortOrder { get; set; }
}
