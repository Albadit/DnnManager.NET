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

    /// <summary>Which way it goes, for a staged file: ↑ to the server, ↓ from it - before its name; nothing for the others.</summary>
    public string Arrow => Group == ChangeGroup.Staged ? Item?.Direction == SyncDirection.Pull ? "↓ " : "↑ " : "";

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

/// <summary>
/// A file open in the editor, as a tab - VS Code's: a click in the list opens it in the preview tab (its name in italics),
/// which the next click replaces; a double-click keeps it open. A change's tab shows its diff and follows each new
/// comparison; a project file's (opened from the Explorer or a search result) shows the file as it is.
/// </summary>
public sealed class EditorTab : INotifyPropertyChanged
{
    private SyncItem? _item;
    private string? _file;
    private bool _preview;

    /// <summary>A change's diff.</summary>
    public EditorTab(SyncItem item, bool preview)
    {
        _item = item;
        _preview = preview;
    }

    /// <summary>A project's file, as it is - <paramref name="path"/> relative to the project.</summary>
    public EditorTab(string path, bool preview)
    {
        _file = path;
        _preview = preview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The change it shows the diff of - null for a project's file.</summary>
    public SyncItem? Item
    {
        get => _item;
        set
        {
            _item = value;
            if (value is not null) _file = null;
            RaiseAll();
        }
    }

    /// <summary>A project's file shown as it is: its path - null for a change's diff.</summary>
    public string? File
    {
        get => _file;
        set
        {
            _file = value;
            if (value is not null) _item = null;
            RaiseAll();
        }
    }

    public bool IsFile => _file is not null;

    /// <summary>Where the file opened from a search result is shown: its line and match, once.</summary>
    public (int Line, int Start, int Length)? RevealAt { get; set; }

    /// <summary>Replaced by the next file opened with a click - until a double-click (on it, or its row) keeps it.</summary>
    public bool IsPreview
    {
        get => _preview;
        set
        {
            _preview = value;
            Raise(nameof(IsPreview));
            Raise(nameof(NameStyle));
        }
    }

    public string Path => _item?.Path ?? _file ?? "";
    public string Name => DeployPath.Name(Path);
    public string Folder => DeployPath.Folder(Path);
    public string Letter => ChangeRow.LetterOf(_item);
    public string Icon => FileIcons.Glyph(Path);
    public Brush IconBrush => FileIcons.Brush(Path);
    public FontStyle NameStyle => IsPreview ? FontStyles.Italic : FontStyles.Normal;

    public string Tip => (_item is { } item ? $"{item.Path} - {item.Describe()}" : $"{Path} - in the project") +
        (IsPreview ? " (preview: double-click to keep it open)" : "");

    /// <summary>What a screen reader says of it in Open Editors.</summary>
    public string Spoken => _item is { } item ? $"{Name}, {item.Describe()}" : $"{Name}, in the project";

    private void RaiseAll()
    {
        foreach (var property in new[] { nameof(Item), nameof(File), nameof(IsFile), nameof(Path), nameof(Name), nameof(Folder), nameof(Letter), nameof(Icon),
                     nameof(IconBrush), nameof(Tip), nameof(Spoken) })
            Raise(property);
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// A row of Source Control's Explorer: a file or folder of the project, indented by how deep it is - one flat list, so
/// a site of thousands of files scrolls as one and makes only the rows on screen. A changed file has its letter.
/// </summary>
public sealed class ExplorerRow(ProjectEntry entry, int depth, string letter) : INotifyPropertyChanged
{
    private bool _expanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectEntry Entry { get; } = entry;
    public int Depth { get; } = depth;
    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public string Letter { get; } = letter;

    /// <summary>A folder open, its rows below it.</summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
        }
    }

    /// <summary>12 px a level, as VS Code indents its tree.</summary>
    public Thickness Indent => new(Depth * 12, 0, 0, 0);

    public string Chevron => !IsFolder ? "" : Expanded ? "" : "";
    public string Icon => IsFolder ? "" : FileIcons.Glyph(Path);
    public Brush IconBrush => FileIcons.Brush(Path);
    public string Spoken => IsFolder ? $"{Name}, folder, {(Expanded ? "expanded" : "collapsed")}" : string.IsNullOrEmpty(Letter) ? Name : $"{Name}, changed";
}

/// <summary>
/// A row of Source Control's Search results: a file (its name, folder and how many matches), or a match on one of its
/// lines - a little before it, the match marked, the rest.
/// </summary>
public sealed class SearchRow
{
    public required FoundFile File { get; init; }

    /// <summary>The match - null for the file's row.</summary>
    public FindMatch? Match { get; init; }

    public bool Collapsed { get; init; }
    public bool IsFile => Match is null;
    public string Path => File.Path;
    public string Name => DeployPath.Name(File.Path);
    public string Folder => DeployPath.Folder(File.Path);
    public int Count => File.Matches.Count;
    public string Chevron => Collapsed ? "" : "";
    public string Icon => FileIcons.Glyph(File.Path);
    public Brush IconBrush => FileIcons.Brush(File.Path);
    /// <summary>Replace is open: the match is struck through, <see cref="Replacement"/> after it - what Replace All will write.</summary>
    public bool Replacing { get; init; }
    public string Replacement { get; init; } = "";
    public TextDecorationCollection? Strike => Replacing ? TextDecorations.Strikethrough : null;
    public string Before => Match?.Before ?? "";
    public string Text => Match?.Text ?? "";
    public string After => Match?.After ?? "";
    public string Tip => Match is { } m ? $"{File.Path}, line {m.Line}" : $"{File.Path} - {Count} match{(Count == 1 ? "" : "es")}";
    public string Spoken => Match is { } m ? $"Line {m.Line}: {m.Before}{m.Text}{m.After}" : $"{File.Path}, {Count} match{(Count == 1 ? "" : "es")}{(Collapsed ? ", collapsed" : "")}";
}

/// <summary>A search result's file template or a match's.</summary>
public sealed class SearchRowTemplates : DataTemplateSelector
{
    public DataTemplate? File { get; set; }
    public DataTemplate? Match { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is SearchRow { IsFile: true } ? File : Match;
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
public sealed class ServerRow(DeployConnection connection, string projectFolder, SyncScan? scan)
{
    public DeployConnection Connection { get; } = connection;

    /// <summary>The project's folder (its IIS site's).</summary>
    public string ProjectFolder { get; } = projectFolder;

    /// <summary>The folder on this PC it is compared with: the server's own local folder, or else the project's.</summary>
    public string Root { get; } = connection.LocalFolder(projectFolder);

    public string Site => Connection.Site;
    public string Name => Connection.Name;
    public string Detail => $"{Connection.Environment} · {Connection.Host}";

    public string Counts => scan is null ? "" :
        string.Join(" ", new[] { ("↑", scan.Count(SyncDirection.Push)), ("↓", scan.Count(SyncDirection.Pull)), ("!", scan.Count(SyncDirection.Conflict)) }
            .Where(c => c.Item2 > 0).Select(c => $"{c.Item1}{c.Item2}"));

    /// <summary>Green: reached; red: couldn't be the last time; grey: not tried yet.</summary>
    public Brush Health => (Brush)System.Windows.Application.Current.FindResource(
        Connection.LastError is not null ? "ErrorText" : Connection.LastCheckedUtc is not null ? "SuccessText" : "TextMuted");

    public string Tip => $"{Connection.Describe()}{Environment.NewLine}Local folder: {Root}" +
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
