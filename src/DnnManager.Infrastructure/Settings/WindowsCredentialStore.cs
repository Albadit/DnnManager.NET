using System.ComponentModel;
using System.Runtime.InteropServices;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Keeps DNN Manager's secrets in the Windows Credential Manager of the signed-in user, as generic credentials named
/// <c>DnnManager/&lt;name&gt;</c> (Control Panel → Credential Manager → Windows Credentials shows and removes them). Windows
/// encrypts them for this user; nothing is written to the settings.
/// </summary>
public sealed class WindowsCredentialStore : ISecretStore
{
    private const string Prefix = "DnnManager/";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    // CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.
    private const int MaxBlobBytes = 2560;

    public string? Read(string name)
    {
        if (!CredRead(Prefix + name, CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Result Write(string name, string secret)
    {
        var bytes = secret.Length * 2;
        if (bytes > MaxBlobBytes) return Result.Fail("The secret is too long for the Windows Credential Manager.");

        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Prefix + name,
                Comment = "DNN Manager",
                CredentialBlobSize = bytes,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            return CredWrite(ref credential, 0)
                ? Result.Ok()
                : Result.Fail($"Could not save it in the Windows Credential Manager: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
        finally
        {
            // Don't leave the secret behind in unmanaged memory.
            for (var i = 0; i < bytes; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public Result Delete(string name)
    {
        if (CredDelete(Prefix + name, CredTypeGeneric, 0)) return Result.Ok();
        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound
            ? Result.Ok()
            : Result.Fail($"Could not remove it from the Windows Credential Manager: {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
