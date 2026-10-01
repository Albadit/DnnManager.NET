using System.Security.Cryptography;
using System.Text;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// Passwords as ASP.NET's <c>SqlMembershipProvider</c> - DNN's membership provider - stores them with
/// <c>passwordFormat="Hashed"</c>: the hash of the salt followed by the password's UTF-16 bytes, or for a keyed (HMAC)
/// algorithm the hash of the password with the salt as the key, both Base64. DNN 9 and 10 ship with SHA256; older sites
/// often still have SHA1.
/// </summary>
public static class MembershipPasswords
{
    /// <summary>The hash algorithms <c>membership/@hashAlgorithmType</c> can name that this knows.</summary>
    public static bool IsSupported(string algorithm) => Create(algorithm) is { } hash && Dispose(hash);

    /// <summary>A new random salt, as SqlMembershipProvider makes them: 16 bytes, Base64.</summary>
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>The stored form of <paramref name="password"/> with <paramref name="salt"/>, as SqlMembershipProvider.EncodePassword makes it.</summary>
    public static string Hash(string password, string salt, string algorithm)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var saltBytes = Convert.FromBase64String(salt);
        using var hash = Create(algorithm) ?? throw new NotSupportedException($"Unknown hash algorithm '{algorithm}'.");
        if (hash is KeyedHashAlgorithm keyed)
        {
            // The key is the salt - cut to the key's length, or repeated to fill it.
            var key = new byte[keyed.Key.Length];
            if (key.Length <= saltBytes.Length)
            {
                Buffer.BlockCopy(saltBytes, 0, key, 0, key.Length);
            }
            else
            {
                for (var at = 0; at < key.Length; at += saltBytes.Length)
                    Buffer.BlockCopy(saltBytes, 0, key, at, Math.Min(saltBytes.Length, key.Length - at));
            }
            keyed.Key = key;
            return Convert.ToBase64String(keyed.ComputeHash(passwordBytes));
        }

        var all = new byte[saltBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, all, 0, saltBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, all, saltBytes.Length, passwordBytes.Length);
        return Convert.ToBase64String(hash.ComputeHash(all));
    }

    // The names .NET Framework's HashAlgorithm.Create knows for these (case doesn't matter there either). The keyed
    // ones get the same default key length as on .NET Framework: 64 bytes, 128 for SHA384 and SHA512.
    private static HashAlgorithm? Create(string algorithm) => algorithm.Trim().ToUpperInvariant() switch
    {
        "SHA1" or "SHA" or "SYSTEM.SECURITY.CRYPTOGRAPHY.SHA1" => SHA1.Create(),
        "SHA256" or "SHA-256" or "SYSTEM.SECURITY.CRYPTOGRAPHY.SHA256" => SHA256.Create(),
        "SHA384" or "SHA-384" or "SYSTEM.SECURITY.CRYPTOGRAPHY.SHA384" => SHA384.Create(),
        "SHA512" or "SHA-512" or "SYSTEM.SECURITY.CRYPTOGRAPHY.SHA512" => SHA512.Create(),
        "MD5" or "SYSTEM.SECURITY.CRYPTOGRAPHY.MD5" => MD5.Create(),
        "HMACSHA1" => new HMACSHA1(new byte[64]),
        "HMACSHA256" => new HMACSHA256(new byte[64]),
        "HMACSHA384" => new HMACSHA384(new byte[128]),
        "HMACSHA512" => new HMACSHA512(new byte[128]),
        _ => null
    };

    private static bool Dispose(HashAlgorithm hash)
    {
        hash.Dispose();
        return true;
    }
}
