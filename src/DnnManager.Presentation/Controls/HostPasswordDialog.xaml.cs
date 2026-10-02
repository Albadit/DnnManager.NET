using System.Windows;
using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Asks which host account gets which new password - typed twice, or generated and shown. A site with one host account
/// isn't asked which.
/// </summary>
public partial class HostPasswordDialog : Window
{
    private HostPasswordDialog(string site, IReadOnlyList<string> hosts)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        UserBox.ItemsSource = hosts;
        if (hosts.Count > 0) UserBox.SelectedIndex = 0;
        // One host account leaves nothing to choose - the intro names it.
        var only = hosts.Count == 1;
        UserPanel.Visibility = only ? Visibility.Collapsed : Visibility.Visible;
        Intro.Text = (only
            ? $"A new password for '{hosts[0]}', the host (superuser) account of '{site}'."
            : $"A new password for a host (superuser) account of '{site}'.") + " DNN Manager doesn't keep it - note it down.";
        Loaded += (_, _) => NewPassword.Focus();
    }

    /// <summary>The account and its new password, or null when cancelled.</summary>
    public static (string User, string Password)? Show(string site, IReadOnlyList<string> hosts)
    {
        var dialog = new HostPasswordDialog(site, hosts)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? (dialog.UserBox.Text.Trim(), dialog.NewPassword.Password) : null;
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var password = DnnAccountRules.GeneratePassword();
        NewPassword.Password = password;
        Confirm.Password = password;
        NewPassword.Reveal();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        var problem = NewPassword.Password.Length == 0 ? null
            : DnnAccountRules.PasswordProblem(NewPassword.Password)
              ?? (Confirm.Password.Length > 0 && Confirm.Password != NewPassword.Password ? "The two passwords aren't the same." : null);
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && NewPassword.Password.Length > 0 && Confirm.Password == NewPassword.Password;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (UserBox.Text.Trim().Length == 0)
        {
            Problem.Text = "Choose or type the host account.";
            Problem.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}