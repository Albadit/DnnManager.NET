namespace DnnManager.Presentation.Services;

/// <summary>
/// The app's UI thread, for what the shared presentation code (the activity log) hands to it - WPF's dispatcher in the
/// Windows app, Avalonia's in the cross-platform one. Each app sets <see cref="Post"/> as it starts, before anything
/// writes to the log.
/// </summary>
public static class UiThread
{
    /// <summary>Queues the action on the UI thread - always queued, also from the UI thread itself.</summary>
    public static Action<Action> Post { get; set; } = action => action();
}
