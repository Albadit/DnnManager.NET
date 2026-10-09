using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DnnManager.Presentation.Services;

/// <summary>
/// A password on the clipboard, for the moment it is pasted: kept out of Windows' clipboard history (Win+V) and its
/// cloud clipboard - which would sync it to the user's other devices and Microsoft account - and marked for clipboard
/// managers to leave alone. Taken off the clipboard again after <see cref="ClearAfter"/>, unless something else has been
/// copied since.
/// </summary>
public static class SecretClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

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

        var timer = new DispatcherTimer { Interval = ClearAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear();
            }
            catch (ExternalException) { /* another program has it open - it stays */ }
        };
        timer.Start();
        return true;
    }
}
