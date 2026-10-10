using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Themes;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Readable colours in both themes (WCAG 1.4.3, 1.4.11), buttons the keyboard reaches, field errors a screen reader hears,
/// toasts that wait their turn and an Output tab that doesn't grow without end.
/// </summary>
[TestClass]
public sealed class UsabilityTests
{
    // ─── Contrast ─────────────────────────────────────────────────────────

    /// <summary>Text, its background and the least contrast it needs: 4.5:1 for text, 3:1 for a focus ring or an outline.</summary>
    private static readonly (string Fg, string Bg, double Min)[] Pairs =
    [
        .. new[] { "TextPrimary", "TextMuted", "LinkFg" }
            .SelectMany(fg => new[] { "WindowBg", "CardBg", "HoverBg", "SelectionBg", "InputBg" }.Select(bg => (fg, bg, 4.5))),
        ("OnAccent", "Accent", 4.5),
        ("OnAccent", "AccentHover", 4.5),
        ("OnAccent", "DangerBrush", 4.5),
        // The search's current match in the panel's logs, under the log's text.
        ("LogFg", "SearchCurrentBg", 4.5),
        ("SearchCurrentBorder", "LogBg", 3),
        // The Output tab's badges: SUCCESS, WARN, ERROR, CANCELLED.
        ("OutBadgeText", "OutSuccessText", 4.5),
        ("OutBadgeText", "OutWarn", 4.5),
        ("OutBadgeText", "OutError", 4.5),
        ("OutBadgeText", "OutMuted", 4.5),
        // The focus ring, with a pixel of the page between it and the control.
        ("FocusBorder", "WindowBg", 3),
        ("FocusBorder", "CardBg", 3),
        ("FocusBorder", "HoverBg", 3),
    ];

    [TestMethod]
    public void Text_and_focus_rings_have_enough_contrast_in_both_themes()
    {
        var failures = new List<string>();
        WpfUi.Run(() =>
        {
            foreach (var (name, theme) in new (string, ResourceDictionary)[] { ("light", new LightTheme()), ("dark", new DarkTheme()) })
                foreach (var (fg, bg, min) in Pairs)
                {
                    var ratio = Contrast(Colour(theme, fg), Colour(theme, bg));
                    if (ratio < min) failures.Add($"{name}: {fg} on {bg} is {ratio:0.00}:1, needs {min}:1");
                }
        });
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    [DataRow("#CCCCCC", "#9E6A03", 2.90)]
    [DataRow("#FFFFFF", "#369432", 3.86)]
    [DataRow("#FFFFFF", "#000000", 21.0)]
    public void Contrast_is_WCAGs_ratio(string fg, string bg, double expected) =>
        Assert.AreEqual(expected, Contrast((Color)ColorConverter.ConvertFromString(fg), (Color)ColorConverter.ConvertFromString(bg)), 0.01);

    private static Color Colour(ResourceDictionary theme, string key) => ((SolidColorBrush)theme[key]).Color;

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var s = c / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    // ─── Keyboard ─────────────────────────────────────────────────────────

    [TestMethod]
    public void An_icon_button_takes_the_keyboard_and_shows_where_it_is() => WpfUi.Run(() =>
    {
        var button = new Button { Style = (Style)System.Windows.Application.Current!.Resources["IconButton"], Content = "x", ToolTip = "Stop IIS" };
        Assert.IsTrue(button.Focusable);
        Assert.AreSame(System.Windows.Application.Current.Resources["FocusRing"], button.FocusVisualStyle);
    });

    [TestMethod]
    public void The_password_eye_is_reached_and_says_what_it_does() => WpfUi.Run(() =>
    {
        var field = new PasswordInput();
        Layout(field);
        var eye = (System.Windows.Controls.Primitives.ToggleButton)field.FindName("Eye");
        Assert.IsTrue(eye.Focusable);
        Assert.AreEqual("Show password", AutomationProperties.GetName(eye));
        field.Reveal();
        Assert.AreEqual("Hide password", AutomationProperties.GetName(eye));
    });

    // ─── Field errors ─────────────────────────────────────────────────────

    [TestMethod]
    public void A_fields_error_is_its_help_text_while_it_shows() => WpfUi.Run(() =>
    {
        var box = new TextBox();
        var error = new TextBlock { Style = (Style)System.Windows.Application.Current!.Resources["ErrorLine"], Visibility = Visibility.Collapsed };
        FieldError.SetFor(error, box);

        error.Text = "The name is taken.";
        Assert.AreEqual("", AutomationProperties.GetHelpText(box), "hidden: not yet");
        error.Visibility = Visibility.Visible;
        Assert.AreEqual("The name is taken.", AutomationProperties.GetHelpText(box));
        error.Text = "";
        Assert.AreEqual("", AutomationProperties.GetHelpText(box));
    });

    [TestMethod]
    public void A_password_field_passes_its_help_text_on() => WpfUi.Run(() =>
    {
        var field = new PasswordInput();
        AutomationProperties.SetHelpText(field, "At least 7 characters.");
        Layout(field);
        Assert.AreEqual("At least 7 characters.", AutomationProperties.GetHelpText((PasswordBox)field.FindName("Hidden")));
    });

    // ─── Toasts ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Toasts_that_stay_wait_their_turn_and_none_is_lost()
    {
        var queue = new ToastQueue();
        var task = new ToastMessage("The sign-in task was removed", ToastKind.Warning);
        var location = new ToastMessage("DNN Manager is installed where others can change it", ToastKind.Warning);
        var update = new ToastMessage("Updated to 1.8.2", ToastKind.Success, "What's new", () => { });

        Assert.AreSame(task, queue.Add(task));
        Assert.IsNull(queue.Add(location), "waits");
        Assert.IsNull(queue.Add(update), "waits - it has a button");
        Assert.IsNull(queue.Add(location), "the same one isn't added twice");
        Assert.AreEqual(2, queue.Waiting);

        Assert.AreSame(location, queue.Next());
        Assert.AreSame(update, queue.Next());
        Assert.IsNull(queue.Next());
        Assert.IsNull(queue.Current);
    }

    [TestMethod]
    public void A_passing_toast_shows_at_once_and_the_one_that_stays_comes_back()
    {
        var queue = new ToastQueue();
        var error = new ToastMessage("Couldn't save the settings", ToastKind.Error);
        var saved = new ToastMessage("Settings saved", ToastKind.Success);
        var copied = new ToastMessage("Copied", ToastKind.Info);

        queue.Add(error);
        Assert.AreSame(saved, queue.Add(saved), "a passing one shows at once");
        Assert.AreSame(copied, queue.Add(copied), "and gives way to the next");
        Assert.AreSame(error, queue.Next(), "the error comes back");
        Assert.IsNull(queue.Next());
    }

    [TestMethod]
    public void The_toast_view_shows_the_next_when_one_is_closed() => WpfUi.Run(() =>
    {
        var view = new ToastView();
        view.Enqueue(new ToastMessage("First", ToastKind.Warning));
        view.Enqueue(new ToastMessage("Second", ToastKind.Error));
        var message = (TextBlock)view.FindName("Message");
        var more = (TextBlock)view.FindName("More");
        Assert.AreEqual("First", message.Text);
        StringAssert.StartsWith(more.Text, "1 more");
        view.Hide();
        Assert.AreEqual("Second", message.Text);
        Assert.AreEqual(Visibility.Collapsed, more.Visibility);
    });

    // ─── The Output tab ───────────────────────────────────────────────────

    [TestMethod]
    public void A_long_stage_keeps_its_first_and_newest_lines_and_every_warning()
    {
        var lines = new List<OutputLine>();
        var at = new DateTime(2026, 10, 9, 12, 0, 0);
        for (var i = 0; i < 1000; i++)
            OutputCap.Add(lines, new OutputLine(at, i == 500 ? LineLevel.Warn : LineLevel.Info, $"line {i}"), head: 10, tail: 20);

        Assert.AreEqual("line 0", lines[0].Text);
        Assert.AreEqual("line 9", lines[9].Text);
        Assert.IsTrue(lines[10].IsElision);
        Assert.AreEqual("line 999", lines[^1].Text);
        Assert.IsTrue(lines.Any(l => l.Text == "line 500"), "a warning is never left out");
        Assert.AreEqual(10 + 1 + 1 + 20, lines.Count);
        // 1000 written, 10 + 20 shown, the warning kept: 969 left out.
        Assert.AreEqual(969, lines[10].Elided);
        StringAssert.Contains(lines[10].Text, "969 more lines");
    }

    [TestMethod]
    public void A_short_stage_keeps_every_line()
    {
        var lines = new List<OutputLine>();
        for (var i = 0; i < 30; i++) OutputCap.Add(lines, new OutputLine(DateTime.Now, LineLevel.Info, $"line {i}"), head: 10, tail: 20);
        Assert.AreEqual(30, lines.Count);
        Assert.IsFalse(lines.Any(l => l.IsElision));
    }

    private static void Layout(FrameworkElement element, double width = 300, double height = 40)
    {
        element.ApplyTemplate();
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
}
