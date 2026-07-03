using System.Security.Cryptography;

namespace AiRecall.Core.Util;

/// <summary>
/// Hash helpers for screenshot dedup and content fingerprints.
/// </summary>
public static class Hashing
{
    /// <summary>SHA-256 of <paramref name="data"/> as a lowercase hex string.</summary>
    public static string Sha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
