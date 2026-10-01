using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Security;

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Base64 key (at least 32 bytes) for link tokens and OTP hashes. Supplied as a Kubernetes Secret.</summary>
    public string SigningKey { get; set; } = "";
}

/// <summary>HMAC-based token derivation and hashing. Raw link tokens and OTP codes are never stored.</summary>
public sealed class Secrets
{
    private readonly byte[] _key;

    public Secrets(IOptions<SecurityOptions> options)
    {
        _key = string.IsNullOrWhiteSpace(options.Value.SigningKey) ? [] : Convert.FromBase64String(options.Value.SigningKey);
        if (_key.Length < 32)
            throw new InvalidOperationException("Security:SigningKey must be a base64 key of at least 32 bytes.");
    }

    /// <summary>Link token for an invitation. Deterministic, so an idempotent retry can return the same link.</summary>
    public string InvitationToken(Guid invitationId) =>
        Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"invitation:{invitationId:N}")));

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public byte[] HashOtp(Guid challengeId, string code) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"otp:{challengeId:N}:{code}"));

    public static string NewOtpCode(int digits) =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, digits)).ToString(new string('0', digits));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
