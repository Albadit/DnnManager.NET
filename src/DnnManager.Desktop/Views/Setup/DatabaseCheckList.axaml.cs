using Avalonia.Controls;
using Avalonia.Media;
using DnnManager.Application.Abstractions;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>One check of a Test connection: its mark (passed, warning, failed) and what was found.</summary>
public sealed record CheckLine(string Name, string Detail, CheckOutcome Outcome)
{
    public bool IsWarning => Outcome == CheckOutcome.Warning;
    public bool IsFailed => Outcome == CheckOutcome.Failed;

    /// <summary>The icon font's check, warning or cancel glyph on Windows; ✓ ! ✕ elsewhere.</summary>
    public string Mark => MainWindow.HasIconFont
        ? (string)(Avalonia.Application.Current?.FindResource(Outcome switch
        {
            CheckOutcome.Warning => "GlyphWarning", CheckOutcome.Failed => "GlyphCancel", _ => "GlyphCheck"
        }) ?? "")
        : Outcome switch { CheckOutcome.Warning => "!", CheckOutcome.Failed => "✕", _ => "✓" };

    public FontFamily MarkFont => MainWindow.HasIconFont && Avalonia.Application.Current?.FindResource("IconFont") is FontFamily font
        ? font
        : FontFamily.Default;
}

/// <summary>What Test connection found: each check with a mark - passed, a warning, or failed with what to do (Controls/DatabaseCheckList).</summary>
public partial class DatabaseCheckList : UserControl
{
    public DatabaseCheckList()
    {
        InitializeComponent();
    }

    /// <summary>"Testing the connection…" until the result is there.</summary>
    public void ShowTesting()
    {
        Lines.ItemsSource = null;
        Testing.IsVisible = true;
    }

    /// <summary>The checks of <paramref name="report"/>; nothing for null.</summary>
    public void Show(DatabaseCheckReport? report)
    {
        Testing.IsVisible = false;
        Lines.ItemsSource = report?.Checks.Select(c => new CheckLine(c.Name, c.Detail, c.Outcome)).ToList();
    }
}
