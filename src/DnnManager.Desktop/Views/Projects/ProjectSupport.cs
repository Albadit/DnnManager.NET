using System.ComponentModel;
using DnnManager.Application;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.Processes;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>How a notice looks: the Windows app's toast kinds.</summary>
public enum NoticeKind { Info, Success, Warning }

/// <summary>A short notice and, optionally, what its button does ("Open folder").</summary>
public sealed record Notice(string Message, NoticeKind Kind, string? ActionText, Action? Action);

/// <summary>
/// The Windows app's toasts (Controls/ToastView) for the Projects page: shown by the Projects view while it is there
/// (<see cref="ProjectsView"/>), and written to Output either way, so a notice is never lost.
/// </summary>
public static class ProjectNotice
{
    /// <summary>A notice to show - the Projects view shows it in its corner.</summary>
    public static event Action<Notice>? Shown;

    public static void Show(string message, NoticeKind kind, string? actionText = null, Action? action = null)
    {
        if (Program.Services?.GetService<ActivityLog>() is { } log)
        {
            if (kind == NoticeKind.Warning) log.Warn(message);
            else if (kind == NoticeKind.Success) log.Success(message);
            else log.Info(message);
        }
        Shown?.Invoke(new Notice(message, kind, actionText, action));
    }
}

/// <summary>The services the Projects page shares with the window - the one <see cref="ServerStore"/>.</summary>
public static class ProjectsServices
{
    private static ServerStore? _store;

    /// <summary>
    /// The store of the sites the Projects page, the IIS status and keep warm read from - the DI container's when it has
    /// one registered, else one made here once (the app has a single container).
    /// </summary>
    public static ServerStore Store => _store ??= Program.Services.GetService<ServerStore>()
                                                  ?? ActivatorUtilities.CreateInstance<ServerStore>(Program.Services);

    /// <summary>Starts following IIS (once) - only where there is IIS.</summary>
    public static void StartStore()
    {
        if (Platform.Supports(PlatformFeature.Iis)) Store.Start();
    }
}

/// <summary>
/// Opens what the user asked for the way Windows does - an address in the browser, a folder in Explorer - handed to
/// Explorer, which runs as the signed-in user (the Windows app's Shell).
/// </summary>
internal static class ProjectShell
{
    public static void Open(string target, bool quiet = false)
    {
        try
        {
            if (OperatingSystem.IsWindows()) ElevatedStart.Explorer(target);
            else
            {
                var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(opener) { ArgumentList = { target }, UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (!quiet) _ = Dialogs.MessageAsync($"Could not open {target}: {ex.Message}");
        }
    }

    /// <summary>A shell in <paramref name="folder"/>, in a window of its own - the Windows app opens it in its terminal panel.</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var _ = ElevatedStart.Start("powershell.exe", ["-NoExit", "-NoLogo"], folder);
            }
            else ProjectShell.Open(folder);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _ = Dialogs.MessageAsync($"Could not open a terminal in {folder}: {ex.Message}");
        }
    }

    public static void Error(string message) => _ = Dialogs.MessageAsync(message);
}
