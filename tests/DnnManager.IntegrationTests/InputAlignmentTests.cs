using System.Windows;
using System.Windows.Controls;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>The inputs as they are drawn: in a search box the caret stands where the placeholder's text starts.</summary>
[TestClass]
public sealed class InputAlignmentTests
{
    [TestMethod]
    public void The_caret_stands_where_the_placeholder_starts() => Sta(() =>
    {
        var box = new TextBox { Tag = "Go to project", Width = 300, FontSize = 13 };
        box.Style = (Style)Styles()["SearchBox"];
        Layout(box);

        // Where the placeholder's text starts.
        var placeholder = (TextBlock)((Border)box.Template.FindName("Placeholder", box)).Child;
        var placeholderX = placeholder.TranslatePoint(new Point(0, 0), box).X;
        var caretX = box.GetRectFromCharacterIndex(0).X;
        Assert.AreEqual(placeholderX, caretX, 0.5, $"Placeholder at {placeholderX:0.##}, caret at {caretX:0.##}.");

        // Typed text starts where the placeholder did.
        box.Text = "G";
        Layout(box);
        Assert.AreEqual(placeholderX, box.GetRectFromCharacterIndex(0).X, 0.5);
    });

    // The app's control styles, as the app has them: in the application's resources (WpfUi).
    private static ResourceDictionary Styles() => System.Windows.Application.Current!.Resources;

    private static void Layout(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.Measure(new Size(300, 40));
        element.Arrange(new Rect(0, 0, 300, 40));
        element.UpdateLayout();
    }

    private static void Sta(Action body) => WpfUi.Run(body);
}
