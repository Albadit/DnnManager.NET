using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DnnManager.Infrastructure.Terminal;

/// <summary>
/// A program running in a Windows pseudo console (ConPTY): what it prints arrives on <see cref="Output"/> as a
/// stream of text with VT escape sequences, and what is written to <see cref="Input"/> is its keyboard. This is how
/// Windows Terminal and VS Code host a shell. Needs Windows 10 1809 or later.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private readonly IntPtr _console;
    private readonly Process _process;
    // Kept apart: the Process object is disposed with the console.
    private readonly int _processId;
    private int _disposed;

    private PseudoConsole(IntPtr console, Process process, Stream input, Stream output)
    {
        _console = console; _process = process; _processId = process.Id; Input = input; Output = output;
    }

    /// <summary>Drops the rights also when DNN Manager isn't elevated - for the tests, which run without administrator rights.</summary>
    internal static bool DropRightsAlways { get; set; }

    /// <summary>The program's keyboard input (UTF-8).</summary>
    public Stream Input { get; }

    /// <summary>What the program draws (UTF-8 with VT sequences); ends when the console is closed.</summary>
    public Stream Output { get; }

    /// <summary>The program's process ID.</summary>
    public int ProcessId => _processId;

    /// <summary>Completes when the program has exited.</summary>
    public Task Exited => _process.WaitForExitAsync();

    /// <summary>
    /// Starts <paramref name="commandLine"/> in a pseudo console of <paramref name="columns"/> x <paramref name="rows"/>
    /// characters - without DNN Manager's administrator rights (<see cref="UnelevatedToken"/>), unless
    /// <paramref name="asAdministrator"/>. Throws <see cref="UnelevatedUnavailableException"/> when the rights can't be
    /// dropped - it never falls back to an Administrator shell by itself.
    /// </summary>
    public static PseudoConsole Start(string commandLine, string workingDirectory, int columns, int rows, bool asAdministrator = false)
    {
        // Elevated, an unelevated shell gets a token without the rights; not elevated ourselves (the tests), there is
        // nothing to drop.
        using var token = !asAdministrator && (Processes.TrustedPrograms.IsElevated || DropRightsAlways) ? UnelevatedToken.Create() : null;

        // Two pipes: we write the input the console reads, and read the output it writes.
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        var result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out var console);
        // The console has its own copies of these two ends now.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(result, "Could not create a pseudo console (needs Windows 10 version 1809 or later).");
        }

        var attributes = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            // The process is attached to the pseudo console through a startup attribute.
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributePseudoConsole, console, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // "Use these standard handles: none". Without it a program started while our own output is redirected
            // (DNN Manager launched from a script) would write there instead of to the pseudo console.
            startup.StartupInfo.Flags = StartfUseStdHandles;
            // DNN Manager's environment - for an Administrator shell without what would make it (or a program run in it)
            // load other code with those rights (Processes.ChildEnvironment) - and cmd doesn't run a program from the
            // current folder, a site folder an app pool can write to, before PATH.
            environment = Marshal.StringToHGlobalUni(ShellEnvironment(asAdministrator));
            const uint flags = ExtendedStartupInfoPresent | NormalPriorityClass | CreateUnicodeEnvironment;
            var directory = Directory.Exists(workingDirectory) ? workingDirectory : null;
            ProcessInformation info;
            var started = token is null
                ? CreateProcess(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                    directory, ref startup, out info)
                // A token made from DNN Manager's own (a restricted one) needs no special privilege to start a process with.
                : CreateProcessAsUser(token, null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                    environment, directory, ref startup, out info);
            if (!started) throw new Win32Exception();

            Process process;
            try { process = Process.GetProcessById(info.ProcessId); }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
            return new PseudoConsole(console, process,
                new FileStream(inputWrite, FileAccess.Write, 1, false), new FileStream(outputRead, FileAccess.Read, 1, false));
        }
        catch
        {
            ClosePseudoConsole(console);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>
    /// The shell's environment block: name=value strings, sorted, each ended by a NUL, and one more NUL at the end. For an
    /// Administrator shell without the variables that make a program load other code.
    /// </summary>
    private static string ShellEnvironment(bool asAdministrator)
    {
        var variables = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = entry.Value as string;
        if (asAdministrator) Processes.ChildEnvironment.Drop(variables);
        variables["NoDefaultCurrentDirectoryInExePath"] = "1";
        var block = new System.Text.StringBuilder();
        foreach (var (name, value) in variables)
            if (value is not null && name.Length > 0 && !name.Contains('=')) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed == 0) ResizePseudoConsole(_console, new Coord(columns, rows));
    }

    /// <summary>
    /// Closes the console, which ends the program in it (and whatever it started). <see cref="Output"/> must still be
    /// read while this runs - closing waits for the last output to be taken - so it happens on a thread-pool thread.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task.Run(() =>
        {
            ClosePseudoConsole(_console);
            try { Input.Dispose(); } catch (IOException) { }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            _process.Dispose();
        });
    }

    // ─── Win32 ────────────────────────────────────────────────────────────

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    // A shell runs at Normal priority even when it is started while DNN Manager is in efficiency mode (Idle priority),
    // which it would inherit for its whole life otherwise.
    private const uint NormalPriorityClass = 0x00000020;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int StartfUseStdHandles = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(int x, int y)
    {
        public readonly short X = (short)x;
        public readonly short Y = (short)y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, System.Text.StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string? application, System.Text.StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// A token for a program DNN Manager starts for the user - an unelevated terminal: DNN Manager's own token with what
/// makes it Administrator taken out, as Windows' User Account Control makes the signed-in user's everyday token from the
/// elevated one. The Administrators group only denies (it never grants), every privilege but "bypass traverse checking"
/// is gone, it runs at Medium integrity (it can't open or send input to DNN Manager's own windows and processes), and
/// what it makes belongs to the user. A restricted version of the caller's own token is one Windows lets a process start
/// another with, without the "replace a process token" privilege an administrator doesn't have - a token of another
/// process (Explorer's) would need it.
/// </summary>
public static class UnelevatedToken
{
    /// <summary>The token - or <see cref="UnelevatedUnavailableException"/> saying why there is none.</summary>
    public static SafeAccessTokenHandle Create()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out var own))
            throw new UnelevatedUnavailableException($"DNN Manager's own sign-in couldn't be read ({new Win32Exception().Message}).");
        using (own)
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sid = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(sid, 0);
            var pin = GCHandle.Alloc(sid, GCHandleType.Pinned);
            SafeAccessTokenHandle restricted;
            try
            {
                var disable = new SidAndAttributes { Sid = pin.AddrOfPinnedObject() };
                if (!CreateRestrictedToken(own, DisableMaxPrivilege, 1, [disable], 0, null, 0, null, out restricted))
                    throw new UnelevatedUnavailableException($"a token without administrator rights couldn't be made ({new Win32Exception().Message}).");
            }
            finally
            {
                pin.Free();
            }
            try
            {
                using var user = WindowsIdentity.GetCurrent();
                SetMediumIntegrity(restricted);
                SetOwner(restricted, user.User!);
                SetDefaultDacl(restricted, user.User!);
                if (IsAdministrator(restricted))
                    throw new UnelevatedUnavailableException("the token made for it still had administrator rights.");
                return restricted;
            }
            catch
            {
                restricted.Dispose();
                throw;
            }
        }
    }

    /// <summary>Whether <paramref name="token"/> counts as a member of Administrators (a deny-only group doesn't).</summary>
    internal static bool IsAdministrator(SafeAccessTokenHandle token)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Medium (S-1-16-8192): what the user's own programs run at - below DNN Manager's High.
    private static void SetMediumIntegrity(SafeAccessTokenHandle token)
    {
        var medium = new SecurityIdentifier("S-1-16-8192");
        WithSid(medium, pointer =>
        {
            var label = new SidAndAttributes { Sid = pointer, Attributes = SeGroupIntegrity };
            if (!SetTokenInformation(token, TokenIntegrityLevel, ref label, Marshal.SizeOf<SidAndAttributes>() + medium.BinaryLength))
                throw new UnelevatedUnavailableException($"its integrity couldn't be lowered ({new Win32Exception().Message}).");
        });
    }

    // What it makes is the user's: the Administrators group (the elevated token's owner) only denies in it.
    private static void SetOwner(SafeAccessTokenHandle token, SecurityIdentifier user) =>
        WithSid(user, pointer =>
        {
            var owner = pointer;
            if (!SetTokenInformation(token, TokenOwner, ref owner, IntPtr.Size))
                throw new UnelevatedUnavailableException($"its owner couldn't be set ({new Win32Exception().Message}).");
        });

    // The rights on what it makes without rules of its own (its processes, pipes): the user's, SYSTEM's and Administrators'.
    private static void SetDefaultDacl(SafeAccessTokenHandle token, SecurityIdentifier user)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 3);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, GenericAll, sid, false, null));
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var dacl = pin.AddrOfPinnedObject();
            if (!SetTokenInformation(token, TokenDefaultDacl, ref dacl, IntPtr.Size))
                throw new UnelevatedUnavailableException($"its default rights couldn't be set ({new Win32Exception().Message}).");
        }
        finally
        {
            pin.Free();
        }
    }

    private static void WithSid(SecurityIdentifier sid, Action<IntPtr> use)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { use(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    private const uint TokenAssignPrimary = 0x0001, TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x1;
    private const int TokenOwner = 4, TokenDefaultDacl = 6, TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const int GenericAll = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableSidCount, SidAndAttributes[]? sidsToDisable,
        uint deletePrivilegeCount, IntPtr[]? privilegesToDelete, uint restrictedSidCount, SidAndAttributes[]? sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref SidAndAttributes information, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref IntPtr information, int length);
}

/// <summary>
/// A terminal without administrator rights couldn't be started - the message says why. Nothing was started in its
/// place: an Administrator terminal is asked for on its own.
/// </summary>
public sealed class UnelevatedUnavailableException(string reason)
    : InvalidOperationException($"A terminal without administrator rights couldn't be started: {reason}");
