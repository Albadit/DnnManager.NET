using System.Windows;
using System.Windows.Controls;
using DnnManager.Domain;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Asks for a project's new name - checked as it is typed - and whether its host name and its database are renamed with it.
/// </summary>
public partial class RenameProjectDialog : Window
{
    private readonly string _currentName;
    private readonly Func<string, string?> _problem;
    private readonly string? _currentHost;
    private readonly Func<string, string> _hostFor;
    private readonly string? _database;

    private RenameProjectDialog(string currentName, Func<string, string?> problem, string? currentHost, Func<string, string> hostFor, string? database)
    {
        _currentName = currentName;
        _problem = problem;
        _currentHost = currentHost;
        _hostFor = hostFor;
        _database = database;
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"Renaming '{currentName}' changes its folder (when it is in the projects folder), its IIS site and its app pool. " +
                     "Its backups stay under the old name.";
        // Nothing to offer for a project without a host name or without a database of its own.
        HostBox.Visibility = currentHost is null ? Visibility.Collapsed : Visibility.Visible;
        DatabasePanel.Visibility = database is null ? Visibility.Collapsed : Visibility.Visible;
        NameBox.Text = currentName;
        Check();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private string NewName => NameBox.Text.Trim();

    /// <summary>
    /// The new name and what follows it, or null when cancelled. <paramref name="problem"/> says what is wrong with a name (null
    /// when it is fine) - taken, for one; <paramref name="hostFor"/> is the host name a project of that name gets. Without a
    /// <paramref name="currentHost"/> or a <paramref name="database"/>, the choice to rename it isn't offered (and is false).
    /// </summary>
    public static RenameProjectChoice? Show(string currentName, Func<string, string?> problem, string? currentHost, Func<string, string> hostFor, string? database)
    {
        var dialog = new RenameProjectDialog(currentName, problem, currentHost, hostFor, database)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true
            ? new RenameProjectChoice(dialog.NewName, currentHost is not null && dialog.HostBox.IsChecked == true,
                database is not null && dialog.DatabaseBox.IsChecked == true)
            : null;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) Check();
    }

    private void Check()
    {
        var name = NewName;
        // The same name again is no rename - OK just waits, without calling it a mistake. Another casing of it is a rename.
        var unchanged = name == _currentName;
        var problem = name.Length == 0 || unchanged ? null
            : ProjectName.Validate(name) is { Success: false } invalid ? invalid.Error : _problem(name);
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = name.Length > 0 && !unchanged && problem is null;

        // While the box is empty, the texts keep showing the current name rather than an empty one.
        var shown = name.Length > 0 ? name : _currentName;
        if (_currentHost is not null) HostText.Text = $"Also change the host name: {_currentHost} → {_hostFor(shown)}";
        if (_database is not null) DatabaseText.Text = $"Also rename the database: [{_database}] → [{shown}]";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>What Rename project was asked to do: the new name, and whether the host name and the database follow it.</summary>
public sealed record RenameProjectChoice(string NewName, bool RenameHost, bool RenameDatabase);
