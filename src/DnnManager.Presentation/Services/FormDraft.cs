using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DnnManager.Presentation.Controls;

namespace DnnManager.Presentation.Services;

/// <summary>
/// What is typed and chosen on a form, by field name - to put it back after DNN Manager restarts. It reads the named
/// text boxes, check boxes, radio buttons and lists under a page (combo boxes and list boxes by their item, not their
/// position), never a password box or anything in a <see cref="PasswordInput"/>. Putting it back sets the choices
/// first and the text last, so text the page fills in from a choice ends up as it was typed; a list whose items load
/// later gets its item once they are there.
/// </summary>
public static class FormDraft
{
    public static Dictionary<string, string> Capture(DependencyObject root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields(root))
        {
            switch (field)
            {
                case TextBox box: values[box.Name] = box.Text; break;
                case ToggleButton toggle: values[toggle.Name] = toggle.IsChecked switch { true => "true", false => "false", null => "" }; break;
                case Selector list when list.SelectedItem is { } item && KeyOf(list, item) is { } key: values[list.Name] = key; break;
            }
        }
        return values;
    }

    public static void Restore(DependencyObject root, IReadOnlyDictionary<string, string> values)
    {
        var fields = Fields(root).Where(f => values.ContainsKey(f.Name)).ToList();
        foreach (var toggle in fields.OfType<ToggleButton>())
            toggle.IsChecked = values[toggle.Name] switch { "true" => true, "false" => false, _ => null };
        foreach (var list in fields.OfType<Selector>()) Select(list, values[list.Name]);
        foreach (var box in fields.OfType<TextBox>())
            if (box.Text != values[box.Name]) box.Text = values[box.Name];
    }

    /// <summary>The named fields under <paramref name="root"/>, in the order they are on the form.</summary>
    private static IEnumerable<FrameworkElement> Fields(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            // A password, shown or not, is never kept.
            if (child is PasswordInput or PasswordBox) continue;
            if (child is FrameworkElement { Name.Length: > 0 } field && field is TextBox { IsReadOnly: false } or ToggleButton or Selector)
                yield return field;
            foreach (var nested in Fields(child)) yield return nested;
        }
    }

    /// <summary>What tells an item from the others: its tag or text, the property the list shows, or its name.</summary>
    private static string? KeyOf(Selector list, object item)
    {
        if (item is ContentControl control) return control.Tag?.ToString() ?? control.Content?.ToString();
        var type = item.GetType();
        foreach (var property in new[] { list.DisplayMemberPath, "Key", "Name", "Label" })
            if (property is { Length: > 0 } && type.GetProperty(property)?.GetValue(item) is { } value)
                return value.ToString();
        return item.ToString();
    }

    private static void Select(Selector list, string key)
    {
        if (TrySelect(list, key)) return;
        // Not there yet (loaded in the background): selected when it comes - unless the user picks something first.
        var items = (INotifyCollectionChanged)list.Items;
        NotifyCollectionChangedEventHandler? retry = null;
        SelectionChangedEventHandler? chosen = null;
        retry = (_, _) =>
        {
            if (!TrySelect(list, key)) return;
            items.CollectionChanged -= retry;
            list.SelectionChanged -= chosen;
        };
        chosen = (_, e) =>
        {
            if (e.AddedItems.Count == 0 || !list.IsKeyboardFocusWithin && !list.IsMouseOver) return;
            items.CollectionChanged -= retry;
            list.SelectionChanged -= chosen;
        };
        items.CollectionChanged += retry;
        list.SelectionChanged += chosen;
    }

    private static bool TrySelect(Selector list, string key)
    {
        foreach (var item in list.Items)
        {
            if (item is null || KeyOf(list, item) != key) continue;
            if (item is UIElement { IsEnabled: false }) continue;
            list.SelectedItem = item;
            return true;
        }
        return false;
    }
}
