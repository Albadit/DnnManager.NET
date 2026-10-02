using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>
/// An optional column of the Projects table, as its Columns menu lists it. <see cref="Key"/> is what
/// <c>appearance.projectColumns</c> in settings.json stores.
/// </summary>
public sealed class ProjectColumnOption : INotifyPropertyChanged
{
    private bool _isVisible;

    public ProjectColumnOption(string key, string header)
    {
        Key = key; Header = header;
    }

    public string Key { get; }
    public string Header { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class ProjectColumns
{
    /// <summary>
    /// The optional columns, the ones most looked at first: the default ones, then what is only sometimes wanted. The
    /// order of the Columns menu and of the table (ProjectsPage.xaml). The check box, status, name and actions are
    /// always shown.
    /// </summary>
    public static IReadOnlyList<(string Key, string Header)> All { get; } =
    [
        ("url", "Site"),
        ("dnn", "DNN version"),
        ("database", "Database"),
        ("sql", "SQL"),
        ("cpu", "CPU (%)"),
        ("memory", "Memory usage"),
        ("pid", "PID"),
        ("lastStarted", "Last started"),
        ("status", "Status"),
        ("ports", "Port(s)"),
        ("id", "Site ID"),
        ("size", "Size"),
        ("memoryPercent", "Memory (%)"),
        ("disk", "Disk read/write"),
        ("network", "Network I/O"),
        ("path", "Path"),
    ];
}
