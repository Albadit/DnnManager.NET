using System.Windows;
using System.Windows.Controls;

namespace DnnManager.Presentation.Controls;

/// <summary>A password box with an eye button that shows / hides what's in it.</summary>
public partial class PasswordInput : UserControl
{
    // Set while copying the value between the two boxes, so that copy doesn't echo back.
    private bool _syncing;

    public PasswordInput()
    {
        InitializeComponent();
    }

    public string Password
    {
        get => Hidden.Password;
        set
        {
            _syncing = true;
            Hidden.Password = value;
            Shown.Text = value;
            _syncing = false;
            PasswordChanged?.Invoke(this, new RoutedEventArgs());
        }
    }

    /// <summary>Raised whenever the password changes, typed or set.</summary>
    public event RoutedEventHandler? PasswordChanged;

    /// <summary>Puts the keyboard focus in whichever box is showing.</summary>
    public void FocusInput()
    {
        if (Eye.IsChecked == true) Shown.Focus();
        else Hidden.Focus();
    }

    private void Hidden_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Shown.Text = Hidden.Password;
        _syncing = false;
        PasswordChanged?.Invoke(this, e);
    }

    private void Shown_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Hidden.Password = Shown.Text;
        _syncing = false;
        PasswordChanged?.Invoke(this, e);
    }

    private void Eye_Toggled(object sender, RoutedEventArgs e)
    {
        var show = Eye.IsChecked == true;
        var hadFocus = Hidden.IsKeyboardFocusWithin || Shown.IsKeyboardFocusWithin;

        Shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        Eye.ToolTip = show ? "Hide password" : "Show password";

        if (!hadFocus) return;
        FocusInput();
        if (show) Shown.CaretIndex = Shown.Text.Length;
    }
}
