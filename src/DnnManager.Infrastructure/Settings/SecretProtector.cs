using System.Security.Cryptography;
using System.Text;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Encrypts a secret kept in the settings - the SQL Server sa password - with Windows DPAPI for the current user:
/// only this Windows account on this PC can read it back. Not a hash: DNN Manager needs the password itself to sign
/// in to SQL Server. An encrypted value is written as <c>dpapi:&lt;base64&gt;</c>.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    // Ties the encrypted value to DNN Manager's settings, so another program's DPAPI data can't be passed off as it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DnnManager.settings.v1");

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The plain value of <paramref name="value"/> - as it is when it isn't encrypted.</summary>
    /// <exception cref="CryptographicException">Encrypted by another Windows user or on another PC, or damaged.</exception>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        var bytes = Convert.FromBase64String(value[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
}
