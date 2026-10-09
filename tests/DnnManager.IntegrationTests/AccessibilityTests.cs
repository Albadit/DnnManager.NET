using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Themes;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What a screen reader and a Windows Contrast theme get: names for fields and icon buttons, the text of the views that
/// draw their own, readable colours - built with the app's own styles, offscreen.
/// </summary>
[TestClass]
public sealed class AccessibilityTests
{
    [TestMethod]
    public void A_password_field_says_its_name() => WpfUi.Run(() =>
    {
        var field = new PasswordInput();
        AutomationProperties.SetName(field, "Host password");
        Layout(field);
        var inner = (PasswordBox)field.FindName("Hidden");
        Assert.AreEqual("Host password", AutomationProperties.GetName(inner));
    });

    [TestMethod]
    public void An_icon_button_is_named_by_its_tooltips_first_part() => WpfUi.Run(() =>
    {
        var button = new Button
        {
            Style = (Style)System.Windows.Application.Current!.Resources["IconButton"],
            Content = "x",
            ToolTip = "Restart the website's background process - can fix problems; its sessions are lost"
        };
        Layout(button);
        Assert.AreEqual("Restart the website's background process", AutomationProperties.GetName(button));
        StringAssert.StartsWith(AutomationProperties.GetHelpText(button), "Restart the website's background process - can fix");
    });

    [TestMethod]
    [DataRow("Start", "Start")]
    [DataRow("Open the website in your browser (Ctrl+O)", "Open the website in your browser")]
    [DataRow("Find: in the output", "Find")]
    public void The_short_name_is_what_comes_before_the_explanation(string tooltip, string name) =>
        Assert.AreEqual(name, AccessibleName.Of(tooltip));

    [TestMethod]
    public void The_log_says_the_lines_on_screen() => WpfUi.Run(() =>
    {
        var log = new LogView { Width = 600, Height = 200 };
        log.Append([new LogLine("first line"), new LogLine("2026-10-09 [ERROR] second line")], 1000);
        Layout(log, 600, 200);
        var peer = UIElementAutomationPeer.CreatePeerForElement(log);
        Assert.AreEqual("Log", peer.GetName());
        Assert.AreEqual(AutomationControlType.Document, peer.GetAutomationControlType());
        var value = (IValueProvider)peer.GetPattern(PatternInterface.Value);
        StringAssert.Contains(value.Value, "first line");
        StringAssert.Contains(value.Value, "second line");
        Assert.IsTrue(value.IsReadOnly);
    });

    [TestMethod]
    public void Both_themes_have_every_colour_the_other_has()
    {
        var keys = new List<HashSet<string>>();
        WpfUi.Run(() =>
        {
            keys.Add([.. new LightTheme().Keys.OfType<string>()]);
            keys.Add([.. new DarkTheme().Keys.OfType<string>()]);
        });
        CollectionAssert.AreEquivalent(keys[0].Order().ToList(), keys[1].Order().ToList());
        CollectionAssert.IsSubsetOf(new[] { "FocusBorder", "OnAccent" }, keys[0].ToList());
    }

    [TestMethod]
    public void A_contrast_theme_gets_windows_own_colours() => WpfUi.Run(() =>
    {
        var palette = new DarkTheme();
        ThemeManager.UseSystemColors(palette);
        var system = typeof(SystemColors).GetProperties().Where(p => p.PropertyType == typeof(SolidColorBrush))
            .Select(p => (SolidColorBrush)p.GetValue(null)!).ToHashSet();
        foreach (var key in palette.Keys.OfType<string>().Where(k => palette[k] is SolidColorBrush && !k.Contains("Backdrop")))
            Assert.IsTrue(system.Contains((SolidColorBrush)palette[key]), $"{key} isn't one of Windows' colours.");
        Assert.AreSame(SystemColors.HighlightTextBrush, palette["OnAccent"]);
        Assert.AreSame(SystemColors.WindowTextBrush, palette["ErrorText"]);
        Assert.AreSame(SystemColors.WindowBrush, palette["WindowBg"]);
    });

    [TestMethod]
    public void Every_control_that_needs_nothing_else_loads() => WpfUi.Run(() =>
    {
        // Its XAML is read as it is made: a binding, a resource or a name that is wrong fails here, not in front of the user.
        var types = typeof(PasswordInput).Assembly.GetTypes()
            .Where(t => typeof(FrameworkElement).IsAssignableFrom(t) && !t.IsAbstract && !typeof(Window).IsAssignableFrom(t) &&
                        t.GetConstructor(Type.EmptyTypes) is not null && t.Namespace?.StartsWith("DnnManager.Presentation") == true)
            .ToList();
        var failed = new List<string>();
        foreach (var type in types)
        {
            try { Layout((FrameworkElement)Activator.CreateInstance(type)!, 800, 600); }
            catch (Exception ex) { failed.Add($"{type.Name}: {(ex.InnerException ?? ex).Message}"); }
        }
        Assert.IsTrue(types.Count > 10, "Few controls found.");
        Assert.AreEqual(0, failed.Count, string.Join(Environment.NewLine, failed));
    });

    private static void Layout(FrameworkElement element, double width = 300, double height = 40)
    {
        element.ApplyTemplate();
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
}
