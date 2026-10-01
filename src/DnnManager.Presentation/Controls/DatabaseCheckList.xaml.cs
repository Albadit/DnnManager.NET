using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;

namespace DnnManager.Presentation.Controls;

/// <summary>What Test connection found: each check with a mark - passed, a warning, or failed with what to do.</summary>
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
        Testing.Visibility = Visibility.Visible;
    }

    /// <summary>The checks of <paramref name="report"/>; nothing for null.</summary>
    public void Show(DatabaseCheckReport? report)
    {
        Testing.Visibility = Visibility.Collapsed;
        Lines.ItemsSource = report?.Checks;
    }
}