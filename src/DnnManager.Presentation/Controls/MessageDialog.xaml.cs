using System.Windows;
using System.Windows.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The app's message box: a question (two buttons named for what they do) or a warning (OK), in the app's theme
/// rather than the plain Windows one. The default answer is the primary button - Enter picks it, Esc the other one.
/// </summary>
public partial class MessageDialog : Window
{
    private enum Kind { Question, Warning, Danger }

    private bool _answer;

    private MessageDialog(string message, Kind kind)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        Message.Text = message;
        Glyph.Text = kind == Kind.Question ? "" : "";
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, kind switch { Kind.Question => "Accent", Kind.Danger => "ErrorText", _ => "LogWarn" });
    }

    /// <summary>
    /// Asks <paramref name="question"/> with buttons labelled <paramref name="yesText"/> and <paramref name="noText"/>
    /// (e.g. "Remove project" / "Cancel"); true for the first. Closing the window counts as the second.
    /// </summary>
    /// <param name="danger">Something that can't be taken back: the warning sign, and <paramref name="yesText"/> red.</param>
    public static bool Ask(string question, string yesText, string noText, bool defaultYes, bool danger = false)
    {
        var dialog = Create(question, danger ? Kind.Danger : Kind.Question);
        var yes = dialog.AddButton(yesText, primary: defaultYes, answer: true);
        if (danger) yes.SetResourceReference(StyleProperty, "Danger");
        var no = dialog.AddButton(noText, primary: !defaultYes, answer: false);
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
