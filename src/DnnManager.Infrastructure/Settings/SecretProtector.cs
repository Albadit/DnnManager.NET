using System.Security.Cryptography;
using System.Text;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Encrypts a secret kept in the settings - the SQL Server sa password - for the current user (<see cref="UserCipher"/>:
/// DPAPI on Windows, AES-GCM with the user's own key elsewhere): only this account on this computer can read it back. Not
/// a hash: DNN Manager needs the password itself to sign in to SQL Server. An encrypted value is written as
/// <c>dpapi:&lt;base64&gt;</c> or <c>aes:&lt;base64&gt;</c>.
/// </summary>
public static class SecretProtector
{
    // Ties the encrypted value to DNN Manager's settings, so another program's encrypted data can't be passed off as it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DnnManager.settings.v1");

    public static bool IsProtected(string value) => UserCipher.IsProtected(value);

    public static string Protect(string plain) => UserCipher.Protect(Encoding.UTF8.GetBytes(plain), Entropy);

    /// <summary>The plain value of <paramref name="value"/> - as it is when it isn't encrypted.</summary>
    /// <exception cref="CryptographicException">Encrypted by another user, on another computer, or damaged.</exception>
    public static string Unprotect(string value) =>
        IsProtected(value) ? Encoding.UTF8.GetString(UserCipher.Unprotect(value, Entropy)) : value;
}
