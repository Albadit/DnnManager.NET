using Avalonia.Media;
using DnnManager.Application.Deployment;
using DnnManager.Domain;

namespace DnnManager.Desktop.Views;

/// <summary>The groups of Source Control's changes, in VS Code's order.</summary>
public enum ChangeGroup { Staged, Outgoing, Incoming, Conflicts }

/// <summary>A server of a project, as SERVERS lists it: its name, where it is, its last comparison's counts.</summary>
public sealed class ServerRow(DeployConnection connection, string projectFolder, SyncScan? scan)
{
    public DeployConnection Connection { get; } = connection;

    /// <summary>The project's folder - in the projects folder, under the project's name.</summary>
    public string ProjectFolder { get; } = projectFolder;

    /// <summary>The folder on this computer it is compared with: the server's own local folder, or else the project's.</summary>
    public string Root { get; } = connection.LocalFolder(projectFolder);

    public string Site => Connection.Site;
    public string Name => Connection.Name;
    public string Title => $"{Site} · {Name}";
    public string Detail => $"{Connection.Environment} · {Connection.Host}";

    public string Counts => scan is null ? "" :
        string.Join(" ", new[] { ("↑", scan.Count(SyncDirection.Push)), ("↓", scan.Count(SyncDirection.Pull)), ("!", scan.Count(SyncDirection.Conflict)) }
            .Where(c => c.Item2 > 0).Select(c => $"{c.Item1}{c.Item2}"));

    /// <summary>Green: reached; red: couldn't be the last time; grey: not tried yet.</summary>
    public IBrush Health => Connection.LastError is not null ? Brushes.IndianRed : Connection.LastCheckedUtc is not null ? Brushes.MediumSeaGreen : Brushes.Gray;

    public string Tip => $"{Connection.Describe()}{Environment.NewLine}Local folder: {Root}" +
        (Connection.LastError is { } error ? $"{Environment.NewLine}Couldn't connect: {error}" : "") +
        (Connection.LastSyncUtc is { } sync ? $"{Environment.NewLine}Last push or pull: {sync.ToLocalTime():g}" : "");
}

/// <summary>
/// A row of the changes: a group's header (title, count, stage or unstage all), or a file - its name, its folder, its
/// status letter as VS Code writes it (M, A, D, ! for a conflict) and whether it is staged.
/// </summary>
public sealed class ChangeRow
{
    public ChangeGroup Group { get; init; }
    public SyncItem? Item { get; init; }
    public bool IsHeader => Item is null;
    public bool IsFile => Item is not null;

    public string Title { get; init; } = "";
    public int Count { get; init; }
    public bool Collapsed { get; init; }
    public string Chevron => Collapsed ? "▸" : "▾";
    public string HeaderText => $"{Chevron} {Title}  ({Count})";
    public bool CanStageAll { get; init; }
    public bool CanUnstageAll { get; init; }

    public string Path => Item?.Path ?? "";
    public string Name => Item is null ? "" : DeployPath.Name(Item.Path);
    public string Folder => Item is null ? "" : DeployPath.Folder(Item.Path);
    public bool IsStaged { get; init; }
    public bool CanStage => Item?.Direction is SyncDirection.Push or SyncDirection.Pull && !IsStaged;
    public bool CanUnstage => IsFile && IsStaged;

    /// <summary>Which way it goes, for a staged file: ↑ to the server, ↓ from it.</summary>
    public string Arrow => Group == ChangeGroup.Staged ? Item?.Direction == SyncDirection.Pull ? "↓ " : "↑ " : "";

    public string Letter => Item?.Status switch
    {
        SyncStatus.LocalAdded or SyncStatus.RemoteAdded => "A",
        SyncStatus.LocalDeleted or SyncStatus.RemoteDeleted => "D",
        SyncStatus.Conflict => "!",
        null => "",
        _ => "M"
    };

    public IBrush LetterBrush => Letter switch
    {
        "A" => Brushes.MediumSeaGreen,
        "D" => Brushes.IndianRed,
        "!" => Brushes.Goldenrod,
        _ => Brushes.DarkGoldenrod
    };

    public string Tip => Item is null ? Title : $"{Item.Path} - {Item.Describe()}" +
        (Item.Sensitive ? ". It looks like a secret or one environment's configuration: never staged by Stage all, and asked about before it is sent." : "");
}

/// <summary>An operation of the history - VS Code's graph.</summary>
public sealed class OperationRow(DeployOperation operation)
{
    public DeployOperation Operation { get; } = operation;

    public string Arrow => Operation.Kind switch
    {
        DeployOperationKind.Push => "↑",
        DeployOperationKind.Pull => "↓",
        DeployOperationKind.Resolve => "⚑",
        _ => "↺"
    };

    public string Message => $"#{Operation.Id} {Operation.Message}";

    public string When
    {
        get
        {
            var local = Operation.StartedUtc.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("t") : local.ToString("d");
        }
    }

    /// <summary>A failed, cancelled or rolled-back operation is shown faded.</summary>
    public double Opacity => Operation.Status == DeployOperationStatus.Completed && Operation.RolledBackBy is null ? 1 : 0.55;

    public bool CanRollBack => Operation.Status == DeployOperationStatus.Completed && Operation.RolledBackBy is null &&
                               Operation.Kind is DeployOperationKind.Push or DeployOperationKind.Pull or DeployOperationKind.Resolve;

    public string Tip => $"#{Operation.Id} {Operation.Kind} by {Operation.User}, {Operation.StartedUtc.ToLocalTime():g} - {Result}";

    public string Result => Operation.Status switch
    {
        DeployOperationStatus.Completed when Operation.RolledBackBy is { } by => $"rolled back by #{by}",
        DeployOperationStatus.Completed => $"{Operation.FileCount} file(s)" + (Operation.Retries > 0 ? $", {Operation.Retries} retried" : ""),
        DeployOperationStatus.Running => "running",
        DeployOperationStatus.Cancelled => "cancelled - put back",
        _ => "failed - put back" + (Operation.Error is { } e ? $": {e}" : "")
    };
}

/// <summary>A line of the diff: unified (one column, +/-) or side by side (the old on the left, the new on the right).</summary>
public sealed class DiffRow
{
    public string OldNumber { get; init; } = "";
    public string NewNumber { get; init; } = "";
    public string Sign { get; init; } = " ";
    public string Text { get; init; } = "";
    public string LeftText { get; init; } = "";
    public string RightText { get; init; } = "";
    public IBrush? Background { get; init; }
    public IBrush? LeftBackground { get; init; }
    public IBrush? RightBackground { get; init; }
    public bool IsHunk { get; init; }
    public FontWeight Weight => IsHunk ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>A run of the Output panel - an operation and what it said.</summary>
public sealed class OutputRow
{
    public string Text { get; init; } = "";
    public IBrush? Foreground { get; init; }
}
