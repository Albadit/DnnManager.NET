using System.ComponentModel;
using System.Diagnostics;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation;

/// <summary>
/// Opens what the user asked for the way Windows does - an address in the browser, a folder in Explorer, a file in its
/// program. Handed to Explorer, which runs as the signed-in user: the browser (or editor) doesn't start with DNN
/// Manager's Administrator rights, and a page it opens can't use them.
/// </summary>
internal static class Shell
{
    /// <summary>Opens <paramref name="target"/>; a failure is said in a message - or, when <paramref name="quiet"/>, not.</summary>
    public static void Open(string target, bool quiet = false)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (!quiet) Dialogs.Error($"Could not open {target}: {ex.Message}");
        }
    }
}
