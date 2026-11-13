using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DnnManager.Application.Deployment;
using DnnManager.Domain;

namespace DnnManager.Desktop.Views;

/// <summary>The groups of Source Control's changes, in VS Code's order.</summary>
public enum ChangeGroup { Staged, Outgoing, Incoming, Conflicts }

/// <summary>The side bar's views, as VS Code's: Source Control, the Explorer, Search.</summary>
public enum SideView { SourceControl, Explorer, Search }

/// <summary>
/// The icons of Source Control: Segoe Fluent Icons' glyphs where the font is (Windows), a character that reads the same
/// elsewhere (macOS, Linux) - with <see cref="Font"/> as the TextBlock's font.
/// </summary>
public static class Icon
{
    private static string G(string glyph, string fallback) => MainWindow.HasIconFont ? glyph : fallback;

    public static FontFamily Font { get; } = MainWindow.HasIconFont ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : FontFamily.Default;
    public static string Add => G("", "+");
    public static string Dash => G("", "−");
    public static string Refresh => G("", "⟳");
    public static string More => G("", "…");
    public static string ChevronDown => G("", "⌄");
    public static string ChevronRight => G("", "›");
    public static string ChevronUp => G("", "⌃");
    public static string Close => G("", "×");
    public static string Cancel => G("", "✕");
    public static string Upload => G("", "↑");
    public static string Download => G("", "↓");
    public static string Undo => G("", "↺");

    /// <summary>A file's icon, by the kind of file - a coloured square where there is no icon font.</summary>
    public static string File(string glyph) => G(glyph, "▪");
}

/// <summary>The theme's brushes, by key - as the theme is now.</summary>
internal static class Res
{
    public static IBrush Brush(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : Brushes.Gray;
}

/// <summary>VS Code's status letters, as style classes: M modified, A added, D deleted, ! conflict - each its colour.</summary>
public interface ILettered
{
    string Letter { get; }
}

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
    public IBrush Health => Res.Brush(Connection.LastError is not null ? "ErrorText" : Connection.LastCheckedUtc is not null ? "SuccessText" : "TextMuted");

    public string Tip => $"{Connection.Describe()}{Environment.NewLine}Local folder: {Root}" +
        (Connection.LastError is { } error ? $"{Environment.NewLine}Couldn't connect: {error}" : "") +
        (Connection.LastSyncUtc is { } sync ? $"{Environment.NewLine}Last push or pull: {sync.ToLocalTime():g}" : "");
}

/// <summary>
/// A row of the changes: a group's header (title, count, stage or unstage all), or a file - its name, its folder, its
/// status letter as VS Code writes it (M, A, D, ! for a conflict) and whether it is staged.
/// </summary>
public sealed class ChangeRow : ILettered
{
    public ChangeGroup Group { get; init; }
    public SyncItem? Item { get; init; }
    public bool IsHeader => Item is null;
    public bool IsFile => Item is not null;

    // ─── A header ────────────────────────────────────────────────────────────

    public string Title { get; init; } = "";
    public int Count { get; init; }
    public bool Collapsed { get; init; }
    public string Chevron => Collapsed ? Views.Icon.ChevronRight : Views.Icon.ChevronDown;
    public bool CanStageAll { get; init; }
    public bool CanUnstageAll { get; init; }

    // ─── A file ──────────────────────────────────────────────────────────────

    public string Path => Item?.Path ?? "";
    public string Name => Item is null ? "" : DeployPath.Name(Item.Path);
    public string Folder => Item is null ? "" : DeployPath.Folder(Item.Path);
    public bool IsStaged { get; init; }
    public bool CanStage => Item?.Direction is SyncDirection.Push or SyncDirection.Pull && !IsStaged;
    public bool CanUnstage => IsFile && IsStaged;

    /// <summary>Which way it goes, for a staged file: ↑ to the server, ↓ from it - before its name.</summary>
    public string Arrow => Group == ChangeGroup.Staged ? Item?.Direction == SyncDirection.Pull ? "↓ " : "↑ " : "";

    public string Letter => LetterOf(Item);
    public string Icon => FileIcons.Glyph(Path);
    public IBrush IconBrush => FileIcons.Brush(Path);

    public static string LetterOf(SyncItem? item) => item?.Status switch
    {
        SyncStatus.LocalAdded or SyncStatus.RemoteAdded => "A",
        SyncStatus.LocalDeleted or SyncStatus.RemoteDeleted => "D",
        SyncStatus.Conflict => "!",
        null => "",
        _ => "M"
    };

    public string Tip => Item is null ? "Open or close the group" : $"{Item.Path} - {Item.Describe()}" +
        (Item.Sensitive ? ". It looks like a secret or one environment's configuration: never staged by Stage all, and asked about before it is sent." : "");

    public string Spoken => Item is null ? $"{Title}, {Count} files{(Collapsed ? ", collapsed" : "")}" : $"{Item.Path}, {Item.Describe()}{(IsStaged ? ", staged" : "")}";
}

/// <summary>
/// A file open in the editor, as a tab - VS Code's: a click in a list opens it in the preview tab (its name in italics),
/// which the next click replaces; a double-click keeps it open. A change's tab shows its diff and follows each new
/// comparison; a project file's (opened from the Explorer or a search result) shows the file as it is.
/// </summary>
public sealed class EditorTab : INotifyPropertyChanged, ILettered
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
            Raise(nameof(Tip));
        }
    }

    public string Path => _item?.Path ?? _file ?? "";
    public string Name => DeployPath.Name(Path);
    public string Folder => DeployPath.Folder(Path);
    public string Letter => ChangeRow.LetterOf(_item);
    public string Icon => FileIcons.Glyph(Path);
    public IBrush IconBrush => FileIcons.Brush(Path);
    public FontStyle NameStyle => IsPreview ? FontStyle.Italic : FontStyle.Normal;

    public string Tip => (_item is { } item ? $"{item.Path} - {item.Describe()}" : $"{Path} - in the project") +
        (IsPreview ? " (preview: double-click to keep it open)" : "");

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
/// A row of the Explorer: a file or folder of the project, indented by how deep it is - one flat list, so a site of
/// thousands of files scrolls as one and makes only the rows on screen. A changed file has its letter.
/// </summary>
public sealed class ExplorerRow(ProjectEntry entry, int depth, string letter) : INotifyPropertyChanged, ILettered
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

    public string Chevron => !IsFolder ? "" : Expanded ? Views.Icon.ChevronDown : Views.Icon.ChevronRight;
    public string Icon => IsFolder ? "" : FileIcons.Glyph(Path);
    public IBrush IconBrush => FileIcons.Brush(Path);
    public string Spoken => IsFolder ? $"{Name}, folder, {(Expanded ? "expanded" : "collapsed")}" : string.IsNullOrEmpty(Letter) ? Name : $"{Name}, changed";
}

/// <summary>
/// A row of the Search results: a file (its name, folder and how many matches), or a match on one of its lines - a
/// little before it, the match marked, the rest.
/// </summary>
public sealed class SearchRow
{
    public required FoundFile File { get; init; }

    /// <summary>The match - null for the file's row.</summary>
    public FindMatch? Match { get; init; }

    public bool Collapsed { get; init; }
    public bool IsFile => Match is null;
    public bool IsMatch => Match is not null;
    public string Path => File.Path;
    public string Name => DeployPath.Name(File.Path);
    public string Folder => DeployPath.Folder(File.Path);
    public int Count => File.Matches.Count;
    public string Chevron => Collapsed ? Views.Icon.ChevronRight : Views.Icon.ChevronDown;
    public string Icon => FileIcons.Glyph(File.Path);
    public IBrush IconBrush => FileIcons.Brush(File.Path);

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

/// <summary>A file's icon and its colour, by its extension - mid tones that read on the light and the dark theme alike.</summary>
public static class FileIcons
{
    private const string Code = "", Image = "";

    private static readonly Dictionary<string, (string Glyph, string Color)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = (Code, "#9B6BC9"), [".vb"] = (Code, "#9B6BC9"),
        [".js"] = (Code, "#C9A400"), [".mjs"] = (Code, "#C9A400"), [".ts"] = (Code, "#3178C6"), [".tsx"] = (Code, "#3178C6"),
        [".css"] = (Code, "#3B8EEA"), [".scss"] = (Code, "#C6538C"), [".less"] = (Code, "#3B8EEA"),
        [".html"] = (Code, "#E37933"), [".htm"] = (Code, "#E37933"), [".ascx"] = (Code, "#E37933"), [".aspx"] = (Code, "#E37933"),
        [".cshtml"] = (Code, "#E37933"), [".xml"] = (Code, "#E37933"), [".dnn"] = (Code, "#E37933"),
        [".json"] = (Code, "#CC8C2B"), [".config"] = (Code, "#CC8C2B"), [".resx"] = (Code, "#CC8C2B"),
        [".sql"] = (Code, "#D25252"), [".sqldataprovider"] = (Code, "#D25252"),
        [".png"] = (Image, "#26A69A"), [".jpg"] = (Image, "#26A69A"), [".jpeg"] = (Image, "#26A69A"), [".gif"] = (Image, "#26A69A"),
        [".svg"] = (Image, "#26A69A"), [".ico"] = (Image, "#26A69A"), [".webp"] = (Image, "#26A69A"),
        [".dll"] = ("", "#8A8A8A"), [".zip"] = ("", "#8A8A8A")
    };

    private static readonly Dictionary<string, IBrush> Brushes = [];

    public static string Glyph(string path) => Icon.File(ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var icon) ? icon.Glyph : "");

    public static IBrush Brush(string path)
    {
        if (!ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var icon)) return Res.Brush("TextMuted");
        if (Brushes.TryGetValue(icon.Color, out var made)) return made;
        return Brushes[icon.Color] = new ImmutableSolidColorBrush(Color.Parse(icon.Color));
    }
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

    public bool CanRollBack => Operation is { Status: DeployOperationStatus.Completed, RolledBackBy: null, ReviewFolder: null };

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
public sealed class OperationFileRow(DeployOperationFile file) : ILettered
{
    public DeployOperationFile File { get; } = file;
    public string Name => DeployPath.Name(File.Path);
    public string Folder => DeployPath.Folder(File.Path);
    public string Where => File.Side == SyncSide.Remote ? "· on the server" : "· in the project";
    public string Letter => File.Action switch { FileAction.Added => "A", FileAction.Deleted => "D", _ => "M" };
}

/// <summary>A run of the Output panel - an operation and what it said.</summary>
public sealed class OutputRow
{
    public string Text { get; init; } = "";
    public IBrush? Foreground { get; init; }
}
