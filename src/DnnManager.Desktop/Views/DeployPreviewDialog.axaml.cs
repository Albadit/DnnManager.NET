using Avalonia.Controls;
using Avalonia.Interactivity;
using DnnManager.Desktop.Services;
using DnnManager.Domain;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views;

/// <summary>A row of the review: what is done to a file, the file, its size.</summary>
public sealed record PlanRow(string What, string Path, string Size);

/// <summary>
/// The dry run of a push or pull: each file and what will be done to it, before anything is. Production asks for a tick
/// that the changes were reviewed; files that look like secrets or one environment's configuration (web.config, keys)
/// ask for one of their own; deletions are listed first. Cancel is the default - Enter changes nothing. As the Windows
/// app's Controls/DeployPreviewDialog.
/// </summary>
public partial class DeployPreviewDialog : Window
{
    private ReviewConsent? _result;

    /// <summary>For the designer only.</summary>
    public DeployPreviewDialog() : this(true, new DeployConnection { Site = "", Name = "", Host = "", UserName = "", RemoteRoot = "" }, [], "")
    {
    }

    internal DeployPreviewDialog(bool push, DeployConnection connection, IReadOnlyList<SyncItem> items, string message)
    {
        InitializeComponent();
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
        Plan.ItemsSource = items.OrderBy(i => i.IsDeletion ? 0 : 1).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(i => new PlanRow(
            push
                ? i.Status switch { SyncStatus.LocalDeleted => "Delete on the server", SyncStatus.LocalAdded => "Add to the server", _ => "Replace on the server" }
                : i.Status switch { SyncStatus.RemoteDeleted => "Delete in the project", SyncStatus.RemoteAdded => "Add to the project", _ => "Replace in the project" },
            i.Path + (i.Sensitive ? "  (secret or configuration?)" : ""),
            ByteSize.Format((push ? i.Local : i.Remote)?.Size ?? 0))).ToList();

        var sensitive = items.Where(i => i.Sensitive).Select(i => i.Path).ToList();
        if (sensitive.Count > 0)
        {
            SensitiveBox.IsVisible = true;
            SensitiveText.Text = $"{(push ? "Send" : "Fetch")} {string.Join(", ", sensitive.Take(5))}{(sensitive.Count > 5 ? $" and {sensitive.Count - 5} more" : "")} - " +
                                 (sensitive.Count == 1 ? "it looks" : "they look") + " like secrets or one environment's own configuration.";
        }
        if (connection.IsProduction && (push || deletions > 0))
        {
            ProductionBox.IsVisible = true;
            ProductionText.Text = push
                ? $"I've reviewed these changes - they go to Production ({connection.Host}){(deletions > 0 ? $", and {deletions} file(s) there are deleted" : "")}."
                : $"I've reviewed these {deletions} deletion(s) in the project.";
        }
        PullTarget.IsVisible = !push;
        OkButton.Content = push ? $"Push {items.Count} file(s)" : $"Pull {items.Count} file(s)";
        if (connection.IsProduction && push)
        {
            OkButton.Classes.Remove("primary");
            OkButton.Classes.Add("danger");
        }
        Follow();
        Opened += (_, _) => CancelButton.Focus();
    }

    /// <summary>What was agreed to - null when cancelled.</summary>
    internal ReviewConsent? Result => _result;

    /// <summary>What was agreed to - null when cancelled.</summary>
    /// <param name="preferReview">A pull: into a review folder is chosen at first.</param>
    public static async Task<ReviewConsent?> ShowAsync(bool push, DeployConnection connection, IReadOnlyList<SyncItem> items, string message, bool preferReview = false)
    {
        var dialog = new DeployPreviewDialog(push, connection, items, message);
        if (preferReview) dialog.IntoReview.IsChecked = true;
        if (Dialogs.Owner is { IsVisible: true } owner) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return dialog._result;
    }

    private void Box_Changed(object? sender, RoutedEventArgs e) => Follow();

    private void Follow()
    {
        if (OkButton is null) return;
        OkButton.IsEnabled = (!SensitiveBox.IsVisible || SensitiveBox.IsChecked == true) && (!ProductionBox.IsVisible || ProductionBox.IsChecked == true);
    }

    internal void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        _result = new ReviewConsent(SensitiveBox.IsChecked == true, PullTarget.IsVisible && IntoReview.IsChecked == true);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
