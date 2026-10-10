using System.IO;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application;
using DnnManager.Application.UseCases;
using DnnManager.Presentation.Services;
using Microsoft.Win32;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Asks what Export for deployment makes the package for: the live domains, whether the live site uses HTTPS, the live
/// database's connection string, whether the database goes along, and the folder it is written to - outside the site's
/// folder, so the package doesn't end up in the next one.
/// </summary>
public partial class DeploymentExportDialog : Window
{
    private readonly string _site;
    private readonly string _directory;
    private readonly bool _hasDatabase;

    private DeploymentExportDialog(string site, string directory, string defaultFolder, bool hasDatabase)
    {
        _site = site;
        _directory = directory;
        _hasDatabase = hasDatabase;
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"A package of '{site}' for the live server: the site's files without logs, caches, source control and " +
                     "editor folders, with web.config ready for the server - and the database as a .bacpac, with the live " +
                     "domain as its portal alias. The local site and its database are not changed.";
        // Without a database to export there is nothing to include - the hint says why.
        IncludeDatabaseBox.IsChecked = hasDatabase;
        IncludeDatabaseBox.IsEnabled = hasDatabase;
        NoDatabaseHint.Visibility = hasDatabase ? Visibility.Collapsed : Visibility.Visible;
        FolderBox.Text = defaultFolder;
        FolderHint.Text = $"Gets {site}.zip, {site}.bacpac and DEPLOY.txt.";
        UpdateState();
        Loaded += (_, _) => DomainsBox.Focus();
    }

    /// <summary>What to export, or null when cancelled.</summary>
    public static ExportForDeploymentRequest? Show(string site, string directory, string defaultFolder, bool hasDatabase)
    {
        var dialog = new DeploymentExportDialog(site, directory, defaultFolder, hasDatabase)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Request() : null;
    }

    private ExportForDeploymentRequest Request()
    {
        var connectionString = ConnectionBox.Text.Trim();
        return new ExportForDeploymentRequest
        {
            ProjectName = _site,
            ProjectDirectory = _directory,
            OutputFolder = Path.GetFullPath(FolderBox.Text.Trim()),
            Domains = Domains(out _),
            Https = HttpsBox.IsChecked == true,
            ConnectionString = connectionString.Length > 0 ? connectionString : null,
            IncludeDatabase = _hasDatabase && IncludeDatabaseBox.IsChecked == true
        };
    }

    /// <summary>
    /// The live domains as typed - comma- or space-separated, lower case, each once, in their order (the first is the
    /// primary alias). <paramref name="problem"/> names the first one that isn't a host name, with an optional port.
    /// </summary>
    private IReadOnlyList<string> Domains(out string? problem)
    {
        problem = null;
        var domains = new List<string>();
        foreach (var entry in DomainsBox.Text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsHostName(entry))
            {
                problem ??= $"'{entry}' isn't a host name - enter it like www.example.com or example.com:8080, without http:// or a path.";
                continue;
            }
            var domain = entry.ToLowerInvariant();
            if (!domains.Contains(domain)) domains.Add(domain);
        }
        return domains;
    }

    /// <summary>A DNS host name, optionally followed by :port.</summary>
    private static bool IsHostName(string text)
    {
        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        if (colon >= 0 && !(int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and <= 65535)) return false;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>What's wrong with the folder, or null when the package can go there.</summary>
    private string? FolderProblem()
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0) return "Choose the folder the package goes to.";
        if (!Path.IsPathFullyQualified(folder)) return @"Enter the folder's full path, e.g. C:\Deploy.";
        try
        {
            // A path Windows can't use throws here, for the message below.
            Path.GetFullPath(folder);
            if (_directory.Length == 0) return null;
            // The site's folder itself, or any folder in it: the package would be part of the site it packs.
            return SafePath.IsSameOrInside(folder, _directory) ? "Choose a folder outside the site's folder." : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return @"Enter the folder's full path, e.g. C:\Deploy.";
        }
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateState();
    }

    private void UpdateState()
    {
        if (OkButton is null) return;
        Domains(out var domainProblem);
        var problem = domainProblem ?? FolderProblem();
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Export to folder" };
        if (Directory.Exists(FolderBox.Text.Trim())) dialog.InitialDirectory = FolderBox.Text.Trim();
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (OkButton.IsEnabled) DialogResult = true;
    }
}
