using System.Security.Cryptography;
using System.Text;

namespace HoleLauncherBackend.Contracts;

public sealed class SessionLookup
{
    private readonly byte[] key;

    public SessionLookup(string? encodedKey)
    {
        if (string.IsNullOrWhiteSpace(encodedKey))
            throw new InvalidOperationException("Auth:SessionKey is required (base64).");

        key = Convert.FromBase64String(encodedKey);
        if (key.Length < 32)
            throw new InvalidOperationException("Auth:SessionKey must contain at least 32 random bytes.");
    }

    public string Compute(string sessionId) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(sessionId)));
}
