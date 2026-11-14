namespace Authenticator.Crypto;

public sealed class YubiKeyRegistration
{
    public required string PublicId { get; init; }
    public int? SerialNumber { get; init; }
}
