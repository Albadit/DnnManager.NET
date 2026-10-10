using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DnnManager.Infrastructure.Terminal;
using Microsoft.Win32.SafeHandles;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// Runs a program as the signed-in user - without DNN Manager's administrator rights (<see cref="UnelevatedToken"/>) - for
/// one installed for this account only, which DNN Manager doesn't run with those rights (<see cref="TrustedPrograms"/>):
/// Docker Desktop's per-user install. Swapped by a program of the user's, such a copy gets no more than that program
/// already has. Its standard input and output are pipes, as <see cref="ProcessRunner"/>'s are; it inherits those three
/// handles and nothing else of DNN Manager's.
/// </summary>
internal static class UserProcess
{
    /// <param name="input">Written to its standard input as it is (bytes), then closed - instead of <paramref name="stdin"/>.</param>
    /// <param name="output">Its standard output as it is (bytes) - instead of lines collected and passed to <paramref name="onOutput"/>.</param>
    public static async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> args, IDictionary<string, string?>? env,
        Action<string>? onOutput, string? stdin, Stream? input, Stream? output, TimeSpan? timeout, CancellationToken ct)
    {
        Process process;
        FileStream toChild, fromChild, errorsFromChild;
        try
        {
            (process, toChild, fromChild, errorsFromChild) = Start(program, args, env);
        }
        catch (Exception ex) when (ex is Win32Exception or UnelevatedUnavailableException)
        {
            return new ProcessResult { ExitCode = -1, StdErr = $"Could not start {program} as you (without administrator rights): {ex.Message}" };
        }

        using (process)
        await using (toChild)
        await using (fromChild)
        await using (errorsFromChild)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var reading = Task.WhenAll(
                output is not null ? fromChild.CopyToAsync(output, CancellationToken.None) : ReadLines(fromChild, stdout, onOutput),
                ReadLines(errorsFromChild, stderr, onOutput));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } after) limit.CancelAfter(after);
            try
            {
                if (input is not null) await input.CopyToAsync(toChild, limit.Token);
                else if (stdin is not null) await toChild.WriteAsync(new UTF8Encoding(false).GetBytes(stdin), limit.Token);
                toChild.Close();
                await process.WaitForExitAsync(limit.Token);
                // What it wrote last is still in the pipes; a helper it left running may keep them open - not waited for long.
                await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Its time is up, it was cancelled, or it stopped reading its input: ended, with what it started.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(grace.Token);
                }
                catch { /* already gone, or not ours to kill */ }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (ex is OperationCanceledException)
                    return new ProcessResult { ExitCode = -1, StdOut = stdout.ToString(), StdErr = $"'{program}' didn't finish within {timeout!.Value.TotalSeconds:0} seconds." };
                if (!process.HasExited) throw;
            }
            return new ProcessResult { ExitCode = process.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
        }
    }

    private static async Task ReadLines(Stream stream, StringBuilder into, Action<string>? onOutput)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (into) into.AppendLine(line);
            onOutput?.Invoke(line);
        }
    }

    /// <summary>Started suspended, put in DNN Manager's job, then let run - so it never runs outside the job.</summary>
    private static (Process, FileStream, FileStream, FileStream) Start(string program, IReadOnlyList<string> args, IDictionary<string, string?>? env)
    {
        using var token = UnelevatedToken.Create();
        var pipes = new List<SafeFileHandle>();
        var attributes = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var handles = IntPtr.Zero;
        try
        {
            // Each pipe: the child's end inheritable, ours not.
            var (stdinRead, stdinWrite) = Pipe(childReads: true, pipes);
            var (stdoutRead, stdoutWrite) = Pipe(childReads: false, pipes);
            var (stderrRead, stderrWrite) = Pipe(childReads: false, pipes);

            // Only these three are inherited - not whatever other handle happens to be inheritable at that moment.
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributeHandleList, handles, (IntPtr)(IntPtr.Size * 3),
                    IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.StartupInfo.StdInput = stdinRead.DangerousGetHandle();
            startup.StartupInfo.StdOutput = stdoutWrite.DangerousGetHandle();
            startup.StartupInfo.StdError = stderrWrite.DangerousGetHandle();
            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(env));

            const uint flags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended;
            if (!CreateProcessAsUser(token, null, new StringBuilder(CommandLine(program, args)), IntPtr.Zero, IntPtr.Zero, true, flags,
                    environment, null, ref startup, out var info))
                throw new Win32Exception();

            Process process;
            try
            {
                process = Process.GetProcessById(info.ProcessId);
                ChildJob.Add(process);
                ResumeThread(info.Thread);
            }
            catch
            {
                TerminateProcess(info.Process, 1);
                throw;
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
            // The child has its own copies of its ends: ours are closed, so its output ends when it does.
            stdinRead.Dispose();
            stdoutWrite.Dispose();
            stderrWrite.Dispose();
            return (process, new FileStream(stdinWrite, FileAccess.Write, 1, false), new FileStream(stdoutRead, FileAccess.Read, 4096, false),
                new FileStream(stderrRead, FileAccess.Read, 4096, false));
        }
        catch
        {
            foreach (var pipe in pipes) pipe.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool childReads, List<SafeFileHandle> pipes)
    {
        var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var read, out var write, ref inherit, 0)) throw new Win32Exception();
        pipes.Add(read);
        pipes.Add(write);
        if (!SetHandleInformation(childReads ? write : read, HandleFlagInherit, 0)) throw new Win32Exception();
        return (read, write);
    }

    /// <summary>
    /// DNN Manager's environment and <paramref name="env"/>'s values, as an environment block - the user's own: the
    /// program runs as them, so nothing in it reaches administrator rights.
    /// </summary>
    private static string EnvironmentBlock(IDictionary<string, string?>? env)
    {
        var variables = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = entry.Value as string;
        if (env is not null)
            foreach (var (name, value) in env) variables[name] = value;
        var block = new StringBuilder();
        foreach (var (name, value) in variables)
            if (value is not null && name.Length > 0 && !name.Contains('=')) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    /// <summary>
    /// The program and its arguments as one command line, each quoted the way the C runtime (and Go, docker's language)
    /// splits it again - as .NET's <see cref="ProcessStartInfo.ArgumentList"/> does.
    /// </summary>
    internal static string CommandLine(string program, IReadOnlyList<string> args)
    {
        var line = new StringBuilder(Quote(program));
        foreach (var arg in args) line.Append(' ').Append(Quote(arg));
        return line.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var quoted = new StringBuilder("\"");
        for (var i = 0; ; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                // Before the closing quote: doubled, so none escapes it.
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(arg[i]);
        }
        return quoted.Append('"').ToString();
    }

    // ─── Win32 ────────────────────────────────────────────────────────────

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
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
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string? application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
