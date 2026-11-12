using System.Security.Cryptography;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Encrypts DNN Manager's secrets - the deploy passwords and keys, the SQL Server sa password - so only the current
/// user can read them back, and a copy of the database alone gives nothing away:
/// <list type="bullet">
/// <item>Windows: DPAPI for the current user (<c>dpapi:&lt;base64&gt;</c>) - this Windows account on this PC.</item>
/// <item>macOS and Linux: AES-256-GCM (<c>aes:&lt;base64 nonce|tag|cipher&gt;</c>) with a random key of the user's own,
/// in a file only they can read (<see cref="KeyFile"/>, mode 600) - kept apart from the database, in the user's
/// configuration folder.</item>
/// </list>
/// As DPAPI, it guards against the database being copied, not against a program running as the same user.
/// </summary>
public static class UserCipher
{
    public const string DpapiPrefix = "dpapi:";
    public const string AesPrefix = "aes:";

    private const int NonceSize = 12, TagSize = 16, KeySize = 32;
    private static readonly Lock KeyLock = new();
    private static byte[]? _key;

    /// <summary>Whether <paramref name="value"/> is a value this class encrypted (on any system).</summary>
    public static bool IsProtected(string value) =>
        value.StartsWith(DpapiPrefix, StringComparison.Ordinal) || value.StartsWith(AesPrefix, StringComparison.Ordinal);

    /// <summary><paramref name="plain"/> encrypted for the current user, with its prefix.</summary>
    /// <param name="purpose">Ties the value to what it is for (DPAPI's entropy, AES-GCM's associated data).</param>
    public static string Protect(byte[] plain, byte[] purpose)
    {
        if (OperatingSystem.IsWindows() && !AesOnWindows)
            return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(plain, purpose, DataProtectionScope.CurrentUser));

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(create: true)!, TagSize)) aes.Encrypt(nonce, plain, cipher, tag, purpose);
        return AesPrefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plain bytes of <paramref name="value"/>.</summary>
    /// <exception cref="CryptographicException">
    /// Encrypted by another user, on another computer or another system, or damaged - or not encrypted at all.
    /// </exception>
    /// <exception cref="FormatException">Not base64 after its prefix.</exception>
    public static byte[] Unprotect(string value, byte[] purpose)
    {
        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException("Encrypted with Windows DPAPI - it can only be read on the Windows it was saved on.");
            return ProtectedData.Unprotect(Convert.FromBase64String(value[DpapiPrefix.Length..]), purpose, DataProtectionScope.CurrentUser);
        }
        if (value.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            var bytes = Convert.FromBase64String(value[AesPrefix.Length..]);
            if (bytes.Length < NonceSize + TagSize) throw new CryptographicException("The encrypted value is damaged.");
            var key = Key(create: false) ?? throw new CryptographicException($"The key it was encrypted with ({KeyFile}) isn't there.");
            var plain = new byte[bytes.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize + TagSize), bytes.AsSpan(NonceSize, TagSize), plain, purpose);
            return plain;
        }
        throw new CryptographicException("The value isn't encrypted.");
    }

    /// <summary>
    /// Where the key is kept on macOS and Linux: <c>~/Library/Application Support/DnnManager/user.key</c> on macOS,
    /// <c>$XDG_CONFIG_HOME/DnnManager/user.key</c> (<c>~/.config</c>) on Linux. Null on Windows (DPAPI keeps its own).
    /// </summary>
    public static string? KeyFile
    {
        get
        {
            if (OperatingSystem.IsWindows()) return null;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folder = OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
                    ? xdg
                    : Path.Combine(home, ".config");
            return Path.Combine(folder, "DnnManager", "user.key");
        }
    }

    /// <summary>A test's own key file (null: the user's).</summary>
    internal static string? KeyFileOverride { get; set; }

    /// <summary>A test on Windows encrypts as macOS and Linux do (with <see cref="KeyFileOverride"/>).</summary>
    internal static bool AesOnWindows { get; set; }

    // The key, read once - made, readable by the user only, when there is none and one is needed to encrypt.
    private static byte[]? Key(bool create)
    {
        lock (KeyLock)
        {
            if (_key is not null && KeyFileOverride is null) return _key;
            var file = KeyFileOverride ?? KeyFile!;
            if (File.Exists(file)) return Keep(Read(file));
            if (!create) return null;

            var key = RandomNumberGenerator.GetBytes(KeySize);
            var folder = Path.GetDirectoryName(file)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Written whole or not at all, never readable by others - not even for a moment.
            var temp = file + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options)) stream.Write(key);
            // Another DNN Manager made one meanwhile: that one is used, this one dropped.
            try { File.Move(temp, file, overwrite: false); }
            catch (IOException) when (File.Exists(file))
            {
                File.Delete(temp);
                return Keep(Read(file));
            }
            return Keep(key);
        }

        static byte[] Read(string file)
        {
            var bytes = File.ReadAllBytes(file);
            return bytes.Length == KeySize ? bytes : throw new CryptographicException($"The key in {file} is damaged - it isn't {KeySize} bytes.");
        }

        // The user's key is kept for the session; a test's isn't.
        static byte[] Keep(byte[] key) => KeyFileOverride is null ? _key = key : key;
    }
}
