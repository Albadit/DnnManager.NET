using DnnManager.Infrastructure.State;

namespace DnnManager.Presentation.Services;

// What DNN Manager keeps between starts besides the settings - one area each in its database's state table, so the
// next start (after a restart, an update, a crash or a normal close) opens where the user was. Only where they were
// and what they typed, never a password; never a dialog, a question, a running operation or a message.

/// <summary>The window: where it was and how big, and the bottom panel's size.</summary>
public sealed class WindowLayout : IStateFile
{
    public static string Area => "window";

    /// <summary>The window's place when it isn't maximized - null until it was first saved.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>The sidebar hidden (Ctrl+B, Customize Layout).</summary>
    public bool SidebarHidden { get; set; }
    /// <summary>The sidebar's width as dragged - null: its density's own.</summary>
    public double? SidebarWidth { get; set; }
    public bool PanelOpen { get; set; }
    public double? PanelHeight { get; set; }
    public bool PanelMaximized { get; set; }
    /// <summary>The width of the terminal list on the panel's right.</summary>
    public double? TerminalListWidth { get; set; }
}

/// <summary>Where the user was: the page, the Projects table as it was set, the project whose Details were open.</summary>
public sealed class WorkspaceState : IStateFile
{
    public static string Area => "workspace";

    /// <summary>The page: Projects, Setup, Existing, Settings or Troubleshoot.</summary>
    public string? Page { get; set; }
    /// <summary>The sidebar page under Settings or Troubleshoot - where closing them goes back to.</summary>
    public string? SidebarPage { get; set; }
    public string? SettingsCategory { get; set; }

    public ProjectsTableState Projects { get; set; } = new();
    /// <summary>The project whose Details were open, and their tab.</summary>
    public string? Project { get; set; }
    public string? ProjectTab { get; set; }
}

/// <summary>The Projects table: its search, filter, sorting, expanded and selected rows, and how far it was scrolled.</summary>
public sealed class ProjectsTableState
{
    public string? Search { get; set; }
    public bool OnlyRunning { get; set; }
    /// <summary>The property the table is sorted by (a column's SortMemberPath); null when it isn't sorted.</summary>
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public List<string> Expanded { get; set; } = [];
    public string? Selected { get; set; }
    public double ScrollOffset { get; set; }
}

/// <summary>
/// What was typed on the forms and not used yet: New project, Host project, and Settings' unsaved changes - by the
/// field's name. Never a password: password boxes aren't read.
/// </summary>
public sealed class FormsState : IStateFile
{
    public static string Area => "forms";

    /// <summary>Per page (Setup, Existing, Settings): field name → value.</summary>
    public Dictionary<string, Dictionary<string, string>> Drafts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The bottom panel's tabs: which one was shown, the Logs tab's site and log, the search.</summary>
public sealed class LogsState : IStateFile
{
    public static string Area => "logs";

    /// <summary>Output, Logs or Terminal.</summary>
    public string? Pane { get; set; }
    public string? LogSite { get; set; }
    /// <summary>The log on the Logs tab: its kind (DNN, IIS, Windows) and title.</summary>
    public string? LogGroup { get; set; }
    public string? LogTitle { get; set; }

    public bool SearchOpen { get; set; }
    public string? SearchText { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegex { get; set; }
}

/// <summary>
/// An update under way: written as DNN Manager closes for it, read and deleted by the version it starts - which then
/// says whether it is the version the update was to install.
/// </summary>
public sealed class UpdateRecord : IStateFile
{
    public static string Area => "update";

    /// <summary>Older than this, the record isn't from the update that started this process.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public DateTime SavedUtc { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    /// <summary>The update helper's result (<c>UpdateResult</c>) - whether the new version was installed, and why not.</summary>
    public string? ResultFile { get; set; }
}

/// <summary>
/// The version that ran last - a newer one starting shows what changed since (What's new), however it was installed:
/// the update, Setup run by hand, a new portable exe.
/// </summary>
public sealed class VersionState : IStateFile
{
    public static string Area => "version";

    public string? LastRun { get; set; }
}

/// <summary>
/// The getting started guide: the version of it the user went through or skipped (<see cref="Onboarding.Version"/>) - 0
/// until then. It decides what a start shows of it (<see cref="Onboarding.AtStart"/>); Help opens it any time.
/// </summary>
public sealed class OnboardingState : IStateFile
{
    public static string Area => "onboarding";

    public int GuideVersion { get; set; }
    /// <summary>Left with Skip guide (or closed) rather than finished.</summary>
    public bool Skipped { get; set; }
    /// <summary>
    /// The version DNN Manager's very first start was - null for someone who used it before this was kept. Still on it,
    /// the user is new: release notes (What's new) would only tell them about changes they never saw, so none are offered.
    /// </summary>
    public string? FirstVersion { get; set; }
}

/// <summary>The commands last run from the command palette, the newest first - listed first as "recently used", as VS Code does.</summary>
public sealed class PaletteState : IStateFile
{
    public static string Area => "palette";

    /// <summary>How many are kept.</summary>
    public const int Kept = 8;

    /// <summary>The commands' ids (<c>project.rename</c>), the newest first.</summary>
    public List<string> Recent { get; set; } = [];
}