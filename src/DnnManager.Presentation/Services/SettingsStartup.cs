using System.Diagnostics;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Loads the settings before the main window opens. When <c>settings.json</c> can't be used, says what is
/// wrong with it instead of crashing, and lets the user fix the file and try again, reset it to the
/// defaults (the old file is kept in <c>backups</c>) or quit.
/// </summary>
internal static class SettingsStartup
{
    private const int TryAgain = 0, OpenFile = 1, Reset = 2, Exit = 3;

    /// <returns>The loaded settings, or null when the user chose to quit.</returns>
    public static SettingsLoadResult? Load(SettingsStore store)
    {
        var resetNotices = new List<SettingsNotice>();
        while (true)
        {
            SettingsException error;
            try
            {
                var result = store.Load();
                return resetNotices.Count == 0 ? result : result with { Notices = [.. resetNotices, .. result.Notices] };
            }
            catch (SettingsException ex)
            {
                error = ex;
            }

            var message = "DNN Manager can't use its settings.\n\n" + error.Message +
                          string.Concat(error.Problems.Select(p => "\n  • " + p)) +
                          "\n\nFix the file and choose Try again, or reset the settings to the defaults " +
                          "(the current file is kept in the backups folder).";

            switch (MessageDialog.Choose(message, ["Try again", "Open file", "Reset to defaults", "Exit"], cancelIndex: Exit))
            {
                case OpenFile:
                    OpenInEditor(store.FilePath);
                    break;
                case Reset:
                    try
                    {
                        var backup = store.ResetToDefaults();
                        resetNotices.Add(new(true, backup is null
                            ? $"Reset {store.FilePath} to the default settings."
                            : $"Reset {store.FilePath} to the default settings - the previous file is in {backup}."));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Dialogs.Error($"Could not reset {store.FilePath}: {ex.Message}");
                    }
                    break;
                case Exit:
                    return null;
            }
        }
    }

    /// <summary>Opens <paramref name="path"/> in the program Windows uses for it, or Notepad when there is none.</summary>
    public static void OpenInEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error($"Could not open {path}: {ex.Message}"); }
        }
    }
}
