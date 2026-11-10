using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.Application.Deployment;
using DnnManager.Domain;

namespace DnnManager.Presentation.Pages;

/// <summary>The groups of the Deploy page's changes, in VS Code's order.</summary>
public enum ChangeGroup { Staged, Outgoing, Incoming, Conflicts }

/// <summary>
/// A row of the Deploy page's changes: a group's header (title, count, stage or unstage all), or a file - its name, its
/// folder, its status letter as VS Code writes it (M, A, D, ! for a conflict) and whether it is staged.
/// </summary>
public sealed class ChangeRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ChangeGroup Group { get; init; }
    public SyncItem? Item { get; init; }
    public bool IsHeader => Item is null;

    // ─── A header ────────────────────────────────────────────────────────────

    public string Title { get; init; } = "";
    public int Count { get; init; }
    public bool Collapsed { get; init; }
    public string Chevron => Collapsed ? "" : "";
    public bool CanStageAll { get; init; }
    public bool CanUnstageAll { get; init; }

    // ─── A file ──────────────────────────────────────────────────────────────

    public string Path => Item?.Path ?? "";
    public string Name => Item is null ? "" : DeployPath.Name(Item.Path);
    public string Folder => Item is null ? "" : DeployPath.Folder(Item.Path);
    public bool IsStaged { get; init; }
    public bool CanStage => Item?.Direction is SyncDirection.Push or SyncDirection.Pull && !IsStaged;

    /// <summary>Which way it goes, for a staged file: ↑ to the server, ↓ from it.</summary>
    public string Arrow => Group == ChangeGroup.Staged ? Item?.Direction == SyncDirection.Pull ? "↓" : "↑" : "";

    public string Letter => LetterOf(Item);

    /// <summary>VS Code's file icon: code, an image, or any other file.</summary>
    public string Icon => FileIcons.Glyph(Path);

    /// <summary>Its colour by the kind of file - as VS Code's icon theme tells C# from JavaScript from CSS at a glance.</summary>
    public Brush IconBrush => FileIcons.Brush(Path);

    public static string LetterOf(SyncItem? item) => item?.Status switch
    {
        SyncStatus.LocalAdded or SyncStatus.RemoteAdded => "A",
        SyncStatus.LocalDeleted or SyncStatus.RemoteDeleted => "D",
        SyncStatus.Conflict => "!",
        null => "",
        _ => "M"
    };

    public string Tip => Item is null ? Title : $"{Item.Path} - {Item.Describe()}" +
        (Item.Sensitive ? ". It looks like a secret or one environment's configuration: never staged by Stage all, and asked about before it is sent." : "");

    public string Spoken => Item is null ? $"{Title}, {Count} files{(Collapsed ? ", collapsed" : "")}" : $"{Item.Path}, {Item.Describe()}{(IsStaged ? ", staged" : "")}";

    internal void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A file's icon and its colour, by its extension - mid tones that read on the light and the dark theme alike.</summary>
public static class FileIcons
{
    private static readonly Dictionary<string, (string Glyph, string Color)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = ("", "#9B6BC9"), [".vb"] = ("", "#9B6BC9"),
        [".js"] = ("", "#C9A400"), [".mjs"] = ("", "#C9A400"), [".ts"] = ("", "#3178C6"), [".tsx"] = ("", "#3178C6"),
        [".css"] = ("", "#3B8EEA"), [".scss"] = ("", "#C6538C"), [".less"] = ("", "#3B8EEA"),
        [".html"] = ("", "#E37933"), [".htm"] = ("", "#E37933"), [".ascx"] = ("", "#E37933"), [".aspx"] = ("", "#E37933"),
        [".cshtml"] = ("", "#E37933"), [".xml"] = ("", "#E37933"), [".dnn"] = ("", "#E37933"),
        [".json"] = ("", "#CC8C2B"), [".config"] = ("", "#CC8C2B"), [".resx"] = ("", "#CC8C2B"),
        [".sql"] = ("", "#D25252"), [".sqldataprovider"] = ("", "#D25252"),
        [".png"] = ("", "#26A69A"), [".jpg"] = ("", "#26A69A"), [".jpeg"] = ("", "#26A69A"), [".gif"] = ("", "#26A69A"),
        [".svg"] = ("", "#26A69A"), [".ico"] = ("", "#26A69A"), [".webp"] = ("", "#26A69A"),
        [".dll"] = ("", "#8A8A8A"), [".zip"] = ("", "#8A8A8A")
    };

    private static readonly Dictionary<string, Brush> Brushes = [];

    public static string Glyph(string path) => ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var icon) ? icon.Glyph : "";

    public static Brush Brush(string path)
    {
        if (!ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var icon))
            return (Brush)System.Windows.Application.Current.FindResource("TextMuted");
        if (Brushes.TryGetValue(icon.Color, out var made)) return made;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(icon.Color));
        brush.Freeze();
        return Brushes[icon.Color] = brush;
    }
}

/// <summary>A header's template or a file's - the changes are one list, so it scrolls as one and makes only the rows on screen.</summary>
public sealed class ChangeRowTemplates : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? File { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ChangeRow { IsHeader: true } ? Header : File;
}

/// <summary>A server in the Deploy page's list: its project, name, environment and host, how it was last reached, and what it has to push and pull.</summary>
public sealed class ServerRow(DeployConnection connection, string root, SyncScan? scan)
{
    public DeployConnection Connection { get; } = connection;

    /// <summary>The project's folder.</summary>
    public string Root { get; } = root;

    public string Site => Connection.Site;
    public string Name => Connection.Name;
    public string Detail => $"{Connection.Environment} · {Connection.Host}";

    public string Counts => scan is null ? "" :
        string.Join(" ", new[] { ("↑", scan.Count(SyncDirection.Push)), ("↓", scan.Count(SyncDirection.Pull)), ("!", scan.Count(SyncDirection.Conflict)) }
            .Where(c => c.Item2 > 0).Select(c => $"{c.Item1}{c.Item2}"));

    /// <summary>Green: reached; red: couldn't be the last time; grey: not tried yet.</summary>
    public Brush Health => (Brush)System.Windows.Application.Current.FindResource(
        Connection.LastError is not null ? "ErrorText" : Connection.LastCheckedUtc is not null ? "SuccessText" : "TextMuted");

    public string Tip => $"{Connection.Describe()}" +
        (Connection.LastError is { } error ? $"{Environment.NewLine}Couldn't connect: {error}" : "") +
        (Connection.LastSyncUtc is { } sync ? $"{Environment.NewLine}Last push or pull: {sync.ToLocalTime():g}" : "");
}

/// <summary>An operation of the history, as the Deploy page lists it - VS Code's graph.</summary>
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

/// <summary>A file of an operation of the history.</summary>
public sealed class OperationFileRow(DeployOperationFile file)
{
    public DeployOperationFile File { get; } = file;
    public string Name => DeployPath.Name(File.Path);
    public string Folder => DeployPath.Folder(File.Path);
    public string Where => File.Side == SyncSide.Remote ? "· on the server" : "· in the project";
    public string Letter => File.Action switch { FileAction.Added => "A", FileAction.Deleted => "D", _ => "M" };
}
