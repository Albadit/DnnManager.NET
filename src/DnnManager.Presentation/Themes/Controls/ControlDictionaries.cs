using System.Windows;
using System.Windows.Controls;

namespace DnnManager.Presentation.Themes;

// The shared control styles, one dictionary per kind of control - merged in App.xaml after the palette and Tokens.

public partial class ButtonStyles : ResourceDictionary
{
    public ButtonStyles() => InitializeComponent();
}

public partial class InputStyles : ResourceDictionary
{
    public InputStyles() => InitializeComponent();

    // The ✕ in a search field (the SearchBox style): empties it and leaves the keyboard there.
    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { TemplatedParent: TextBox box }) return;
        box.Clear();
        box.Focus();
    }
}

public partial class SelectionStyles : ResourceDictionary
{
    public SelectionStyles() => InitializeComponent();
}

public partial class MenuStyles : ResourceDictionary
{
    public MenuStyles() => InitializeComponent();
}

public partial class ListStyles : ResourceDictionary
{
    public ListStyles() => InitializeComponent();
}

public partial class LayoutStyles : ResourceDictionary
{
    public LayoutStyles() => InitializeComponent();
}
