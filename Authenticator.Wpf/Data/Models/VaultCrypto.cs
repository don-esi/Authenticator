using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>Singleton-ish row describing shared VDEK recovery material.</summary>
public class VaultCrypto
{
    [Key]
    public int Id { get; set; } = 1;

    public int CryptoFormatVersion { get; set; } = 1;

    [Required]
    [MaxLength(128)]
    public string VdekFingerprint { get; set; } = string.Empty;

    /// <summary>VDEK wrapped under recovery key.</summary>
    [Required]
    public string RecoveryWrap { get; set; } = string.Empty;

    [Required]
    public string RecoverySalt { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedAt { get; set; }
}
