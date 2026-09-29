using System.Windows;
using System.Windows.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The app's message box: a question (Yes / No) or a warning (OK), in the app's theme rather than the plain
/// Windows one. The default answer is the primary button - Enter picks it, Esc answers No.
/// </summary>
public partial class MessageDialog : Window
{
    private enum Kind { Question, Warning }

    private bool _answer;

    private MessageDialog(string message, Kind kind)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        Message.Text = message;
        Glyph.Text = kind == Kind.Question ? "" : "";
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, kind == Kind.Question ? "Accent" : "LogWarn");
    }

    /// <summary>Asks <paramref name="question"/>; true for Yes. Closing the window counts as No.</summary>
    public static bool Ask(string question, bool defaultYes)
    {
        var dialog = Create(question, Kind.Question);
        var yes = dialog.AddButton("Yes", primary: defaultYes, answer: true);
        var no = dialog.AddButton("No", primary: !defaultYes, answer: false);
        no.IsCancel = true;
        (defaultYes ? yes : no).IsDefault = true;
        dialog.Loaded += (_, _) => (defaultYes ? yes : no).Focus();
        return dialog.ShowDialog() == true && dialog._answer;
    }

    /// <summary>Shows <paramref name="message"/> as a warning with an OK button.</summary>
    public static void Warn(string message)
    {
        var dialog = Create(message, Kind.Warning);
        var ok = dialog.AddButton("OK", primary: true, answer: true);
        ok.IsDefault = ok.IsCancel = true;
        dialog.Loaded += (_, _) => ok.Focus();
        dialog.ShowDialog();
    }

    /// <summary>
    /// Shows <paramref name="message"/> as a warning with one button per choice and returns the index of the
    /// one clicked. The first choice is the default (Enter); Esc or closing the window picks <paramref name="cancelIndex"/>.
    /// </summary>
    public static int Choose(string message, IReadOnlyList<string> choices, int cancelIndex)
    {
        var dialog = Create(message, Kind.Warning);
        var chosen = cancelIndex;
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = dialog.AddButton(choices[i], primary: i == 0, answer: true);
            button.Click += (_, _) => chosen = index;
            button.IsDefault = i == 0;
            button.IsCancel = i == cancelIndex;
            if (i == 0) dialog.Loaded += (_, _) => button.Focus();
        }
        return dialog.ShowDialog() == true ? chosen : cancelIndex;
    }

    private static MessageDialog Create(string message, Kind kind)
    {
        var dialog = new MessageDialog(message, kind)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog;
    }

    private Button AddButton(string text, bool primary, bool answer)
    {
        var button = new Button { Content = text, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        if (primary) button.SetResourceReference(StyleProperty, "Primary");
        button.Click += (_, _) =>
        {
            _answer = answer;
            DialogResult = true;
        };
        Buttons.Children.Add(button);
        return button;
    }
}
