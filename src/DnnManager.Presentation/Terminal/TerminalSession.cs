using System.Text;
using System.Windows.Threading;
using DnnManager.Infrastructure.Terminal;

namespace DnnManager.Presentation.Terminal;

/// <summary>
/// A shell running in a pseudo console, with the <see cref="TerminalBuffer"/> its output is drawn into. The output is
/// read on a thread-pool thread and applied on the UI thread in batches, so a program that prints a lot doesn't
/// queue one UI call per chunk.
/// </summary>
internal sealed class TerminalSession : IDisposable
{
    private readonly PseudoConsole _console;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly StringBuilder _pending = new();
    private bool _flushQueued;
    private bool _disposed;
    // Writes go one after the other, off the UI thread (a program that isn't reading would block the pipe).
    private Task _writes = Task.CompletedTask;

    public TerminalSession(string commandLine, string workingDirectory, int columns, int rows)
    {
        Buffer = new TerminalBuffer(columns, rows);
        Buffer.Reply += Write;
        _console = PseudoConsole.Start(commandLine, workingDirectory, Buffer.Columns, Buffer.Rows);
        _ = Task.Run(ReadOutput);
        _console.Exited.ContinueWith(_ => _dispatcher.BeginInvoke(() => { if (!_disposed) Exited?.Invoke(this, EventArgs.Empty); }));
    }

    public TerminalBuffer Buffer { get; }

    /// <summary>The shell's process ID.</summary>
    public int ProcessId => _console.ProcessId;

    /// <summary>The screen changed - redraw it.</summary>
    public event EventHandler? Changed;

    /// <summary>The shell printed something - raised on the UI thread for each batch of its output, before <see cref="Changed"/>.</summary>
    public event EventHandler? Output;

    /// <summary>The shell ended by itself (e.g. <c>exit</c>).</summary>
    public event EventHandler? Exited;

    private void ReadOutput()
    {
        var bytes = new byte[8192];
        var chars = new char[8192];
        // Keeps the bytes of a character that is split over two reads.
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            int read;
            while ((read = _console.Output.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                lock (_pending)
                {
                    _pending.Append(chars, 0, count);
                    if (_flushQueued) continue;
                    _flushQueued = true;
                }
                // Below input and rendering, so typing and drawing go on while output pours in.
                _dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The console was closed.
        }
        finally
        {
            _console.Output.Dispose();
        }
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        if (_disposed) return;
        Output?.Invoke(this, EventArgs.Empty);
        Buffer.Feed(text);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends <paramref name="text"/> to the shell as if it was typed.</summary>
    public void Write(string text)
    {
        if (_disposed || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        _writes = _writes.ContinueWith(_ =>
        {
            try
            {
                _console.Input.Write(bytes, 0, bytes.Length);
                _console.Input.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The shell is gone.
            }
        }, TaskScheduler.Default);
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || (columns == Buffer.Columns && rows == Buffer.Rows)) return;
        Buffer.Resize(columns, rows);
        _console.Resize(Buffer.Columns, Buffer.Rows);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the shell and everything running in it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _console.Dispose();
    }
}
