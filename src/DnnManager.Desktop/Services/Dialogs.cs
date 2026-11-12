using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Services;

/// <summary>What was agreed to in a push's or pull's review.</summary>
public sealed record ReviewConsent(bool AllowSensitive, bool IntoReviewFolder);

/// <summary>
/// The app's questions and messages - small modal windows made in code: a question with two answers, a message, a
/// password, and the review of a push or pull (each file and what is done to it; Production asks for a tick).
/// </summary>
public static class Dialogs
{
    /// <summary>The window a dialog belongs to - the main window.</summary>
    public static Window? Owner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task<bool> ConfirmAsync(string question, string yes, string no, bool danger = false) =>
        ShowAsync<bool>("DNN Manager", Text(question), close =>
        [
            Button(no, () => close(false), isCancel: true),
            Button(yes, () => close(true), isDefault: !danger, danger: danger)
        ], false);

    public static Task MessageAsync(string message, string title = "DNN Manager") =>
        ShowAsync<bool>(title, Text(message), close => [Button("OK", () => close(true), isDefault: true, isCancel: true)], true);

    /// <summary>A password, asked for - null when cancelled.</summary>
    public static Task<string?> PasswordAsync(string prompt, string ok)
    {
        var box = new TextBox { PasswordChar = '•', Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Spacing = 10, Children = { Text(prompt), box } };
        return ShowAsync<string?>("DNN Manager", panel, close =>
        [
            Button("Cancel", () => close(null), isCancel: true),
            Button(ok, () => close(box.Text ?? ""), isDefault: true)
        ], null, box);
    }

    /// <summary>
    /// The dry run of a push or pull: each file and what will be done to it, before anything is. A file that looks like
    /// a secret, and Production, each ask for a tick first - as the Windows app's review does.
    /// </summary>
    public static Task<ReviewConsent?> ReviewAsync(bool push, DeployConnection connection, IReadOnlyList<SyncItem> items, string message)
    {
        var where = $"{connection.Name} ({connection.Environment}, {connection.Host})";
        var deletions = items.Count(i => i.IsDeletion);
        var transfers = items.Count - deletions;
        var panel = new StackPanel { Spacing = 10, Width = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = push ? $"Push {items.Count} file(s) to {where}" : $"Pull {items.Count} file(s) from {where}",
            FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(Text((push
                                    ? $"{transfers} sent to the server, {deletions} deleted from it - nothing else on the server changes. "
                                    : $"{transfers} fetched into the project, {deletions} deleted from it - nothing else in the project changes. ") +
                                (connection.KeepBackups ? "What is replaced or deleted is kept first, to roll back to." : "Backups are off for this connection: this can't be rolled back.") +
                                (message.Trim().Length > 0 ? $"{Environment.NewLine}Message: {message.Trim()}" : "")));
        var plan = new ListBox
        {
            MaxHeight = 280,
            ItemsSource = items.OrderBy(i => i.IsDeletion ? 0 : 1).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(i =>
                (push
                    ? i.Status switch { SyncStatus.LocalDeleted => "Delete on the server", SyncStatus.LocalAdded => "Add to the server", _ => "Replace on the server" }
                    : i.Status switch { SyncStatus.RemoteDeleted => "Delete in the project", SyncStatus.RemoteAdded => "Add to the project", _ => "Replace in the project" })
                + "   " + i.Path + (i.Sensitive ? "  (secret or configuration?)" : "") + "   " + ByteSize.Format((push ? i.Local : i.Remote)?.Size ?? 0)).ToList()
        };
        panel.Children.Add(plan);

        var ticks = new List<CheckBox>();
        var sensitive = items.Where(i => i.Sensitive).Select(i => i.Path).ToList();
        CheckBox? sensitiveBox = null;
        if (sensitive.Count > 0)
        {
            sensitiveBox = Tick($"{(push ? "Send" : "Fetch")} {string.Join(", ", sensitive.Take(5))}{(sensitive.Count > 5 ? $" and {sensitive.Count - 5} more" : "")} - " +
                                (sensitive.Count == 1 ? "it looks" : "they look") + " like secrets or one environment's own configuration.");
            ticks.Add(sensitiveBox);
            panel.Children.Add(sensitiveBox);
        }
        if (connection.IsProduction && (push || deletions > 0))
        {
            var production = Tick(push
                ? $"I've reviewed these changes - they go to Production ({connection.Host}){(deletions > 0 ? $", and {deletions} file(s) there are deleted" : "")}."
                : $"I've reviewed these {deletions} deletion(s) in the project.");
            ticks.Add(production);
            panel.Children.Add(production);
        }
        CheckBox? review = null;
        if (!push)
        {
            review = new CheckBox { Content = "Into a review folder instead - the project stays as it is" };
            panel.Children.Add(review);
        }

        return ShowAsync<ReviewConsent?>(push ? "Review the push" : "Review the pull", panel, close =>
        {
            var ok = Button(push ? $"Push {items.Count} file(s)" : $"Pull {items.Count} file(s)",
                () => close(new ReviewConsent(sensitiveBox?.IsChecked == true, review?.IsChecked == true)),
                isDefault: true, danger: connection.IsProduction && push);
            void Follow() => ok.IsEnabled = ticks.All(t => t.IsChecked == true);
            foreach (var tick in ticks) tick.IsCheckedChanged += (_, _) => Follow();
            Follow();
            return [Button("Cancel", () => close(null), isCancel: true), ok];
        }, null);
    }

    // ─── Making them ────────────────────────────────────────────────────────

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };

    private static CheckBox Tick(string text) =>
        new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 } };

    private static Button Button(string text, Action click, bool isDefault = false, bool isCancel = false, bool danger = false)
    {
        var button = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (isDefault && !danger) button.Classes.Add("accent");
        if (danger) button.Background = new SolidColorBrush(Color.Parse("#C42B1C"));
        if (danger) button.Foreground = Brushes.White;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// Shows <paramref name="body"/> with the buttons <paramref name="buttons"/> makes (given how to close with an answer),
    /// modal to the main window; closed otherwise (Esc, the window's ×), the answer is <paramref name="dismissed"/>.
    /// </summary>
    private static async Task<T> ShowAsync<T>(string title, Control body, Func<Action<T>, IReadOnlyList<Button>> buttons, T dismissed,
        Control? focus = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(title, body, buttons, dismissed, focus));

        var answer = dismissed;
        var window = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 360
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons(value => { answer = value; window.Close(); })) row.Children.Add(button);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { body, row } };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        if (focus is not null) window.Opened += (_, _) => focus.Focus();
        if (Owner is { IsVisible: true } owner) await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return answer;
    }
}

/// <summary>Answers the use cases' questions with the app's dialogs - asked from the thread pool, shown on the UI thread.</summary>
public sealed class AvaloniaUserPrompt : IUserPrompt
{
    public Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default) =>
        Dialogs.ConfirmAsync(question, yes, no);

    public Task<bool> ConfirmDangerAsync(string question, string yes, string no, CancellationToken ct = default) =>
        Dialogs.ConfirmAsync(question, yes, no, danger: true);
}
