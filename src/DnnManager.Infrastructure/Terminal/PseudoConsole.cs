using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>The program's keyboard input (UTF-8).</summary>
    public Stream Input { get; }

    /// <summary>What the program draws (UTF-8 with VT sequences); ends when the console is closed.</summary>
    public Stream Output { get; }

    /// <summary>The program's process ID.</summary>
    public int ProcessId => _processId;

    /// <summary>Completes when the program has exited.</summary>
    public Task Exited => _process.WaitForExitAsync();

    /// <summary>Starts <paramref name="commandLine"/> in a pseudo console of <paramref name="columns"/> x <paramref name="rows"/> characters.</summary>
    public static PseudoConsole Start(string commandLine, string workingDirectory, int columns, int rows)
    {
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
            if (!CreateProcess(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | NormalPriorityClass, IntPtr.Zero, Directory.Exists(workingDirectory) ? workingDirectory : null,
                    ref startup, out var info))
                throw new Win32Exception();

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
        }
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

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
