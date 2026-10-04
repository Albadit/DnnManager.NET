using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Loads the settings before the main window opens. When they can't be used (saved by a newer DNN Manager, or the
/// database can't be read), says what is wrong instead of crashing, and lets the user try again, reset them to the
/// defaults or quit.
/// </summary>
internal static class SettingsStartup
{
    private const int TryAgain = 0, Reset = 1, Exit = 2;

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
                          "\n\nChoose Try again once what is named above is fixed (another program holding the database, " +
                          "say), or reset the settings to the defaults.";

            switch (MessageDialog.Choose(message, ["Try again", "Reset to defaults", "Exit"], cancelIndex: Exit))
            {
                case Reset:
                    try
                    {
                        store.ResetToDefaults();
                        resetNotices.Add(new(true, $"Reset the settings in {store.Location} to the defaults."));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Dialogs.Error($"Could not reset the settings in {store.Location}: {ex.Message}");
                    }
                    break;
                case Exit:
                    return null;
            }
        }
    }
}
