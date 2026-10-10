using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DnnManager.Presentation.Services;

/// <summary>
/// A password on the clipboard, for the moment it is pasted: kept out of Windows' clipboard history (Win+V) and its
/// cloud clipboard - which would sync it to the user's other devices and Microsoft account - and marked for clipboard
/// managers to leave alone. Taken off the clipboard again after <see cref="ClearAfter"/>, or when DNN Manager quits
/// before that (<see cref="ClearIfStillOurs"/>) - unless something else has been copied since.
/// </summary>
public static class SecretClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    // The password put on the clipboard last, until it is taken off again - what quitting takes off. UI thread only.
    private static string? _pending;

    /// <summary>Puts <paramref name="secret"/> on the clipboard as described; false when the clipboard was busy.</summary>
    public static bool Set(string secret)
    {
        try
        {
            var data = new DataObject();
            data.SetText(secret);
            // Windows' own formats for "don't keep this": a DWORD 0 for the history and the cloud, and the mere presence of
            // the monitor format for clipboard managers.
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (ExternalException)
        {
            return false;
        }
        _pending = secret;

        var timer = new DispatcherTimer { Interval = ClearAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ClearIf(secret);
        };
        timer.Start();
        return true;
    }

    /// <summary>
    /// Takes the password put on the clipboard last off it, when it is still there - as DNN Manager quits: the copy on
    /// the clipboard outlives it (it was put there to stay), and the timer that would take it off doesn't.
    /// </summary>
    public static void ClearIfStillOurs()
    {
        if (_pending is { } secret) ClearIf(secret);
    }

    private static void ClearIf(string secret)
    {
        if (_pending == secret) _pending = null;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear();
        }
        catch (ExternalException) { /* another program has it open - it stays */ }
    }
}
