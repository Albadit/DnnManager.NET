using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Whether the keyboard or the mouse was used last - for a look that belongs to the keyboard alone. A select's list
/// (ComboBoxItem) marks the item the arrow keys are on with WPF's <c>IsHighlighted</c>, but the pointer moves that mark
/// too and it stays on the last item passed after the pointer has left; shown only while <see cref="Keyboard"/>, it
/// follows the keys, and the pointer's own hover is <c>IsMouseOver</c>.
///
/// <para>XAML binds <c>{Binding Keyboard, Source={x:Static s:InputMode.Current}}</c> - an instance, as a binding to a
/// static property written in XAML doesn't follow its changes. Only a pointer that really moved counts: WPF also reports
/// a move when the list scrolls under a still pointer.</para>
/// </summary>
public sealed class InputMode : INotifyPropertyChanged
{
    public static InputMode Current { get; } = new();

    private bool _keyboard;
    private Point _cursor;

    private InputMode() =>
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            var input = e.StagingItem.Input;
            if (input.RoutedEvent == System.Windows.Input.Keyboard.PreviewKeyDownEvent) Set(true);
            else if (input.RoutedEvent == Mouse.PreviewMouseDownEvent || input.RoutedEvent == Mouse.PreviewMouseWheelEvent) Set(false);
            else if (input.RoutedEvent == Mouse.PreviewMouseMoveEvent && GetCursorPos(out var at) && at != _cursor)
            {
                _cursor = at;
                Set(false);
            }
        };

    /// <summary>A key was pressed after the pointer last moved or clicked.</summary>
    public bool Keyboard => _keyboard;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(bool keyboard)
    {
        if (_keyboard == keyboard) return;
        _keyboard = keyboard;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keyboard)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct Point(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
