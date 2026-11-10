using System.Windows;
using DnnManager.Domain;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>What the user agreed to in the preview of a push or pull.</summary>
public sealed record DeployConsent(bool AllowSensitive, bool IntoReviewFolder);

/// <summary>
/// The dry run of a push or pull: each file and what will be done to it, before anything is. Production asks for a tick
/// that the changes were reviewed; files that look like secrets or one environment's configuration (web.config, keys)
/// ask for one of their own; deletions are listed apart. Cancel is the default - Enter changes nothing.
/// </summary>
public partial class DeployPreviewDialog : Window
{
    private sealed record Row(string What, string Path, string Size);

    internal DeployPreviewDialog(bool push, DeployConnection connection, IReadOnlyList<SyncItem> items, string message)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        var where = $"{connection.Name} ({connection.Environment}, {connection.Host})";
        var deletions = items.Count(i => i.IsDeletion);
        var transfers = items.Count - deletions;
        Title = push ? "Review the push" : "Review the pull";
        Heading.Text = push ? $"Push {items.Count} file(s) to {where}" : $"Pull {items.Count} file(s) from {where}";
        Summary.Text = (push
                           ? $"{transfers} sent to the server, {deletions} deleted from it - nothing else on the server changes. "
                           : $"{transfers} fetched into the project, {deletions} deleted from it - nothing else in the project changes. ") +
                       (connection.KeepBackups ? "What is replaced or deleted is kept first, to roll back to." : "Backups are off for this connection: this can't be rolled back.") +
                       (message.Trim().Length > 0 ? $"{Environment.NewLine}Message: {message.Trim()}" : "");
        Plan.ItemsSource = items.OrderBy(i => i.IsDeletion ? 0 : 1).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(i => new Row(
            push
                ? i.Status switch { SyncStatus.LocalDeleted => "Delete on the server", SyncStatus.LocalAdded => "Add to the server", _ => "Replace on the server" }
                : i.Status switch { SyncStatus.RemoteDeleted => "Delete in the project", SyncStatus.RemoteAdded => "Add to the project", _ => "Replace in the project" },
            i.Path + (i.Sensitive ? "  (secret or configuration?)" : ""),
            ByteSize.Format((push ? i.Local : i.Remote)?.Size ?? 0))).ToList();

        var sensitive = items.Where(i => i.Sensitive).Select(i => i.Path).ToList();
        if (sensitive.Count > 0)
        {
            SensitiveBox.Visibility = Visibility.Visible;
            SensitiveText.Text = $"{(push ? "Send" : "Fetch")} {string.Join(", ", sensitive.Take(5))}{(sensitive.Count > 5 ? $" and {sensitive.Count - 5} more" : "")} - " +
                                 (sensitive.Count == 1 ? "it looks" : "they look") + " like secrets or one environment's own configuration.";
        }
        if (connection.IsProduction && (push || deletions > 0))
        {
            ProductionBox.Visibility = Visibility.Visible;
            ProductionText.Text = push
                ? $"I've reviewed these changes - they go to Production ({connection.Host}){(deletions > 0 ? $", and {deletions} file(s) there are deleted" : "")}."
                : $"I've reviewed these {deletions} deletion(s) in the project.";
        }
        PullTarget.Visibility = push ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = push ? $"Push {items.Count} file(s)" : $"Pull {items.Count} file(s)";
        if (connection.IsProduction && push) OkButton.Style = (Style)FindResource("Danger");
        Box_Changed(this, new RoutedEventArgs());
    }

    /// <summary>What was agreed to - null when cancelled.</summary>
    /// <param name="preferReview">A pull: into a review folder is chosen at first.</param>
    public static DeployConsent? Show(bool push, DeployConnection connection, IReadOnlyList<SyncItem> items, string message, bool preferReview = false)
    {
        var dialog = new DeployPreviewDialog(push, connection, items, message)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (preferReview) dialog.IntoReview.IsChecked = true;
        return dialog.ShowDialog() == true ? new DeployConsent(dialog.SensitiveBox.IsChecked == true, dialog.IntoReview.IsChecked == true) : null;
    }

    private void Box_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        OkButton.IsEnabled = (SensitiveBox.Visibility != Visibility.Visible || SensitiveBox.IsChecked == true) &&
                             (ProductionBox.Visibility != Visibility.Visible || ProductionBox.IsChecked == true);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
