using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using DnnManager.Application;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Finds what keeps a folder from being deleted, with two Windows mechanisms:
/// <list type="bullet">
/// <item>the Restart Manager (what installers use to find "files in use") for processes with files open, and</item>
/// <item>each process's current directory, read from its PEB - a terminal <c>cd</c>'d into the folder holds
/// no file open, yet Windows won't delete a directory that is some process's working folder.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileLockService(ILogger<FileLockService> log) : IFileLockService
{
    // Never closed: Windows itself, the shell, security software and this app.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "fontdrvhost", "explorer", "sihost", "ctfmon", "MsMpEng", "NisSrv", "SearchIndexer",
        "SearchProtocolHost", "SearchFilterHost", "audiodg", "spoolsv", "conhost"
    };

    // Restart Manager takes the files to check; past this many, the first ones are enough to find the culprits.
    private const int MaxFilesToCheck = 20_000;

    private readonly ILogger<FileLockService> _log = log;

    public IReadOnlyList<LockingProcess> FindLockers(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\');
        var found = new Dictionary<int, LockingProcess>();

        foreach (var (pid, appName, isService) in ProcessesWithFilesOpen(root))
            Add(found, pid, appName, "has files in the folder open", isService);

        if (Environment.Is64BitProcess)
            foreach (var pid in ProcessesWorkingIn(root))
                if (!found.ContainsKey(pid))
                    Add(found, pid, null, "its working folder is inside the project", isService: false);

        return found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Records <paramref name="pid"/> - as the app that owns it - with a readable name.</summary>
    private void Add(Dictionary<int, LockingProcess> found, int pid, string? appName, string reason, bool isService)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // Report a helper (VS Code, Chrome, … run many processes of the same exe) as its topmost
            // same-named ancestor: closing that closes the app, closing a helper only restarts it.
            var owner = OwnerOf(process);
            using var _ = owner == process ? null : owner;
            if (found.ContainsKey(owner.Id)) return;

            var exe = owner.ProcessName;
            var canClose = !isService && owner.Id != Environment.ProcessId && owner.SessionId != 0 && !Protected.Contains(exe);
            found[owner.Id] = new LockingProcess(owner.Id, appName is { Length: > 0 } && owner == process ? appName : FriendlyName(owner),
                exe + ".exe", reason, canClose);
        }
        catch (ArgumentException) { /* exited meanwhile */ }
        catch (InvalidOperationException) { /* exited meanwhile */ }
    }

    public async Task<IReadOnlyList<LockingProcess>> CloseAsync(IReadOnlyList<LockingProcess> processes, CancellationToken ct)
    {
        var stillRunning = new List<LockingProcess>();
        foreach (var locker in processes.Where(p => p.CanClose))
        {
            try
            {
                using var process = Process.GetProcessById(locker.Id);

                // Politely first (the app can save state), then force - with its children, whose handles are the lock too.
                if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow() &&
                    await ExitedAsync(process, TimeSpan.FromSeconds(5), ct))
                    continue;
                process.Kill(entireProcessTree: true);
                if (!await ExitedAsync(process, TimeSpan.FromSeconds(5), ct)) stillRunning.Add(locker);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _log.LogWarning(ex, "Could not close {Process}", locker);
                stillRunning.Add(locker);
            }
        }
        // Windows releases a closed process's handles a moment after it exits.
        await Task.Delay(500, ct);
        return stillRunning;
    }

    private static async Task<bool> ExitedAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    public async Task<IReadOnlyList<string>> StopProgramsRunningFromAsync(string directory)
    {
        var stopped = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || ExePath(process) is not { } exe || !SafePath.IsInside(exe, directory))
                        continue;
                    process.Kill(entireProcessTree: true);
                    await ExitedAsync(process, TimeSpan.FromSeconds(5), CancellationToken.None);
                    stopped.Add(Path.GetFileName(exe));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    _log.LogWarning(ex, "Could not stop {Process} running from {Directory}", process.ProcessName, directory);
                }
            }
        }
        // Windows releases an ended process's handles a moment after it exits.
        if (stopped.Count > 0) await Task.Delay(500);
        return stopped;
    }

    /// <summary>The program a process runs - read with the least access, so other users' and services' processes too.</summary>
    private static string? ExePath(Process process)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }


    public Result ScheduleDeleteOnRestart(string directory)
    {
        if (!Directory.Exists(directory)) return Result.Ok();
        try
        {
            var root = new DirectoryInfo(directory);
            if (SafePath.IsLink(root.FullName))
                return Result.Fail($"{directory} is a link - DNN Manager doesn't delete through those.{Environment.NewLine}{SafePath.LinkHint}");
            // Windows deletes these as it starts - by their paths, with SYSTEM's rights, without the protection against
            // junctions DNN Manager has. So each folder is first made Administrators' and SYSTEM's only (and theirs): no
            // program without administrator rights, nor the site's app pool, can then put a junction on the way between
            // now and the restart. A link already in it is deleted itself, never followed.
            var files = new List<string>();
            var folders = new List<string>();
            Collect(root, files, folders);
            folders = folders.OrderByDescending(d => d.Length).Append(root.FullName).ToList();

            // Pending deletes run in order: every file first, then the folders deepest first so each is empty.
            var failed = 0;
            foreach (var path in files.Concat(folders))
                if (!MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) failed++;
            return failed == 0
                ? Result.Ok()
                : Result.Fail($"{failed} item(s) couldn't be scheduled for deletion (error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The files and folders in <paramref name="dir"/>, each folder made Administrators' and SYSTEM's only before it is
    /// read - so nothing can be swapped into it afterwards. A link (junction, symbolic link) is listed itself and not
    /// gone into: deleting it deletes only the link.
    /// </summary>
    private static void Collect(DirectoryInfo dir, List<string> files, List<string> folders)
    {
        dir.SetAccessControl(AdministratorsOnly());
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            if (entry is not DirectoryInfo sub) files.Add(entry.FullName);
            else
            {
                if (!SafePath.IsLink(sub.FullName)) Collect(sub, files, folders);
                folders.Add(sub.FullName);
            }
        }
    }

    private static DirectorySecurity AdministratorsOnly()
    {
        var security = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    // ─── Restart Manager: processes with files open ──────────────────────────

    private IEnumerable<(int Pid, string AppName, bool IsService)> ProcessesWithFilesOpen(string root)
    {
        // Only the files still there after the failed delete - those are the locked ones (and their neighbours).
        var options = SafePath.Recursive;
        options.IgnoreInaccessible = true;
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", options).Take(MaxFilesToCheck).ToArray()
            : Array.Empty<string>();
        if (files.Length == 0) return Array.Empty<(int, string, bool)>();

        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out var session, 0, key) != 0) return Array.Empty<(int, string, bool)>();
        try
        {
            foreach (var chunk in files.Chunk(1000))
                if (RmRegisterResources(session, (uint)chunk.Length, chunk, 0, null, 0, null) != 0)
                    return Array.Empty<(int, string, bool)>();

            uint needed = 0, count = 0, reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result == 0) return Array.Empty<(int, string, bool)>(); // nobody has them open
            if (result != ERROR_MORE_DATA) return Array.Empty<(int, string, bool)>();

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return Array.Empty<(int, string, bool)>();
            return infos.Take((int)count)
                .Select(i => (i.Process.dwProcessId, i.strAppName,
                    i.ApplicationType is RM_APP_TYPE.RmService or RM_APP_TYPE.RmCritical))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restart Manager lookup failed for {Folder}", root);
            return Array.Empty<(int, string, bool)>();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    // ─── Current directories ─────────────────────────────────────────────────

    private IEnumerable<int> ProcessesWorkingIn(string root)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (CurrentDirectoryOf(process.Id) is { } cwd && SafePath.IsSameOrInside(cwd, root))
                    yield return process.Id;
            }
        }
    }

    /// <summary>
    /// A 64-bit process's current directory: PEB → ProcessParameters → CurrentDirectory.DosPath. Null for
    /// processes that can't be opened (protected, other users) and for 32-bit ones, which keep it elsewhere.
    /// </summary>
    private static string? CurrentDirectoryOf(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64) return null;

            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0 ||
                pbi.PebBaseAddress == IntPtr.Zero)
                return null;

            var parameters = ReadPointer(handle, pbi.PebBaseAddress + 0x20);             // PEB.ProcessParameters
            if (parameters == IntPtr.Zero) return null;
            var header = ReadBytes(handle, parameters + 0x38, 16);                       // CurrentDirectory.DosPath
            if (header is null) return null;
            var length = BitConverter.ToUInt16(header, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(header, 8);
            if (length == 0 || buffer == IntPtr.Zero) return null;
            var text = ReadBytes(handle, buffer, length);
            return text is null ? null : Encoding.Unicode.GetString(text);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = ReadBytes(process, address, IntPtr.Size);
        return bytes is null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    private static byte[]? ReadBytes(IntPtr process, IntPtr address, int size)
    {
        var buffer = new byte[size];
        // Sizes are SIZE_T - pointer-sized - both ways.
        return ReadProcessMemory(process, address, buffer, size, out var read) && read == size ? buffer : null;
    }

    // ─── Process helpers ─────────────────────────────────────────────────────

    /// <summary>The topmost ancestor of <paramref name="process"/> running the same exe (or the process itself).</summary>
    private static Process OwnerOf(Process process)
    {
        var current = process;
        for (var depth = 0; depth < 16; depth++)
        {
            var parentId = ParentIdOf(current.Id);
            if (parentId is null) break;
            try
            {
                var parent = Process.GetProcessById(parentId.Value);
                if (!parent.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                    parent.StartTime > current.StartTime) // a reused pid, not the real parent
                {
                    parent.Dispose();
                    break;
                }
                if (current != process) current.Dispose();
                current = parent;
            }
            catch
            {
                break;
            }
        }
        return current;
    }

    private static int? ParentIdOf(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)pbi.InheritedFromUniqueProcessId
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string FriendlyName(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch { /* no access to its modules */ }
        return process.ProcessName;
    }

    // ─── Win32 ───────────────────────────────────────────────────────────────

    private const int CCH_RM_SESSION_KEY = 32;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private enum RM_APP_TYPE { RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2, RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000 }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
}
