using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using Microsoft.Win32;

namespace DnnManager.Presentation.Controls;

/// <summary>What to do with a project folder that already exists.</summary>
public enum ExistingFolderAction { IisOnly, IisAndDatabase, DatabaseOnly, Redownload }

/// <summary>
/// Asks how to set up an existing project folder - IIS site, local database, or both - and which
/// backup (if any) to restore into the database. Shared by "New project" (when the folder is already
/// there) and "Existing folder".
/// </summary>
public partial class ExistingFolderOptions : UserControl
{
    // File == null means "no backup - keep the database / create it empty".
    private sealed record BackupOption(string? File, string Label);

    private DnnProject? _project;

    public ExistingFolderOptions()
    {
        InitializeComponent();
    }

    /// <summary>Adds the option to extract a fresh DNN package over the folder (used by setup).</summary>
    public bool OfferRedownload
    {
        get => Redownload.Visibility == Visibility.Visible;
        set
        {
            Redownload.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value && Redownload.IsChecked == true) IisAndDatabase.IsChecked = true;
        }
    }

    public ExistingFolderAction Action =>
        IisOnly.IsChecked == true      ? ExistingFolderAction.IisOnly :
        DatabaseOnly.IsChecked == true ? ExistingFolderAction.DatabaseOnly :
        Redownload.IsChecked == true   ? ExistingFolderAction.Redownload :
                                         ExistingFolderAction.IisAndDatabase;

    public bool SetupsDatabase => Action is ExistingFolderAction.IisAndDatabase or ExistingFolderAction.DatabaseOnly;

    /// <summary>The backup to restore, or null for none. Only meaningful when <see cref="SetupsDatabase"/>.</summary>
    public string? BackupFile => (BackupCombo.SelectedItem as BackupOption)?.File;

    /// <summary>Raised when the chosen action changes (setup hides its DNN-package fields unless redownloading).</summary>
    public event EventHandler? ActionChanged;

    /// <summary>Loads the backups found in <paramref name="project"/>: its backups folder or its root, newest first.</summary>
    public void Load(DnnProject project)
    {
        _project = project;
        var found = new[] { project.BackupDirectory, project.ProjectDirectory }
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .Where(LocalSqlContainer.IsBackupFile)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new BackupOption(f.FullName,
                $"{Path.GetRelativePath(project.ProjectDirectory, f.FullName)}  " +
                $"({f.Length / 1024d / 1024d:N1} MB, {f.LastWriteTime:yyyy-MM-dd HH:mm})"))
            .Append(new BackupOption(null, "No backup - keep the database / create it empty"))
            .ToList();

        BackupCombo.ItemsSource = found;
        BackupCombo.SelectedIndex = 0;
    }

    private void Action_Checked(object sender, RoutedEventArgs e)
    {
        if (BackupPanel is null) return; // raised during InitializeComponent
        BackupPanel.Visibility = SetupsDatabase ? Visibility.Visible : Visibility.Collapsed;
        ActionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a database backup",
            Filter = "Database backups (*.bacpac;*.bak)|*.bacpac;*.bak",
            InitialDirectory = _project is { } p && Directory.Exists(p.BackupDirectory) ? p.BackupDirectory : ""
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var options = (BackupCombo.ItemsSource as IEnumerable<BackupOption>)?.ToList() ?? new List<BackupOption>();
        var picked = options.FirstOrDefault(o => string.Equals(o.File, dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (picked is null)
        {
            picked = new BackupOption(dialog.FileName, dialog.FileName);
            options.Insert(0, picked);
            BackupCombo.ItemsSource = options;
        }
        BackupCombo.SelectedItem = picked;
    }
}
