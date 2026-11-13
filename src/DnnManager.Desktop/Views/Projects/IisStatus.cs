using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>
/// The state of IIS (its web service, W3SVC) for the status bar's first cell (the Windows app's Controls/IisStatus), with
/// Restart and Stop while it runs and Start while it's stopped. It shows what the <see cref="ServerStore"/> knows -
/// Windows reports the service's status, so a start or stop from anywhere shows up by itself. Not shown where there is
/// no IIS (macOS, Linux).
/// </summary>
public sealed class IisStatus : UserControl
{
    private readonly ServerStore? _store;
    private readonly OperationRunner _runner = Program.Services.GetRequiredService<OperationRunner>();
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new()
    {
        Margin = new Thickness(9, 0, 0, 1.5), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis, Text = "IIS…"
    };
    private readonly StackPanel _state = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
    private readonly Button _start, _restart, _stop;

    public IisStatus()
    {
        IsVisible = Platform.Supports(PlatformFeature.Iis);
        _start = Button("GlyphPlay", "▶", "Start IIS", "Start IIS, the web server, so your websites can be opened");
        _restart = Button("GlyphRefresh", "↻", "Restart IIS", "Restart IIS - every website on this computer is unavailable for a few seconds; asks first");
        _stop = Button("GlyphStop", "■", "Stop IIS", "Stop IIS - every website on this computer stops until you start it again; asks first");
        _start.Click += (_, _) => Control(IisServerAction.Start);
        _restart.Click += (_, _) => Control(IisServerAction.Restart);
        _stop.Click += (_, _) => Control(IisServerAction.Stop);
        _state.Children.Add(_dot);
        _state.Children.Add(_text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Children = { _start, _restart, _stop } };
        var dock = new DockPanel { Margin = new Thickness(14, 0, 6, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(_state);
        Content = dock;
        Bind(FontSizeProperty, this.GetResourceObservable("TextCaption"));
        if (!IsVisible) return;

        _store = ProjectsServices.Store;
        _store.RuntimeChanged += (_, _) => Dispatcher.UIThread.Post(Show);
        _runner.PropertyChanged += Runner_Changed;
        ActualThemeVariantChanged += (_, _) => Show();
        ProjectsServices.StartStore();
        Show();
    }

    private static Button Button(string glyph, string fallback, string name, string tip)
    {
        var button = new Button
        {
            Classes = { "icon" }, Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 11, IsVisible = false,
            Content = OperatingSystem.IsWindows() ? new TextBlock { Text = ThemeBrush.Glyph(glyph), Classes = { "glyph" }, FontSize = 11 } : new TextBlock { Text = fallback }
        };
        button.Bind(ForegroundProperty, button.GetResourceObservable("TextMuted"));
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private void Runner_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationRunner.Current)) Dispatcher.UIThread.Post(Show);
    }

    /// <summary>What the store knows about IIS now - or, while DNN Manager itself is changing it, what it is doing.</summary>
    private void Show()
    {
        if (_store is null) return;
        var state = _store.Runtime;
        var pending = _store.RuntimePending;
        _text.Text = pending ?? state switch
        {
            IisServerState.Running => "IIS running",
            IisServerState.Stopped => "IIS stopped",
            IisServerState.Starting => "IIS starting…",
            IisServerState.Stopping => "IIS stopping…",
            IisServerState.NotInstalled => "IIS not installed",
            _ => "IIS unknown"
        };
        ToolTip.SetTip(_state, _text.Text + " - " + (state == IisServerState.NotInstalled
            ? "IIS, the web server, isn't installed: Settings → IIS → Set up IIS turns it on."
            : "IIS is the web server every website runs in (the W3SVC service). Running: your websites can be opened."));
        var (stroke, fill) = pending is not null ? ("LogWarn", "LogWarn") : state switch
        {
            IisServerState.Running => ("SuccessText", "SuccessText"),
            IisServerState.Starting or IisServerState.Stopping => ("LogWarn", "LogWarn"),
            IisServerState.NotInstalled or IisServerState.Unknown => ("ErrorText", null),
            _ => ("TextMuted", (string?)null)
        };
        _dot.Stroke = ThemeBrush.Get(stroke, this);
        _dot.Fill = fill is null ? Brushes.Transparent : ThemeBrush.Get(fill, this);
        // The text in the dot's colour, like Docker Desktop's "Engine running".
        _text.Foreground = ThemeBrush.Get(stroke, this);
        var settled = pending is null;
        _start.IsVisible = settled && state == IisServerState.Stopped;
        _restart.IsVisible = _stop.IsVisible = settled && state == IisServerState.Running;
        _start.IsEnabled = _restart.IsEnabled = _stop.IsEnabled = !_runner.IsBusy;
    }

    private async void Control(IisServerAction action)
    {
        if (_store is not null) await _store.ControlIisAsync(action);
    }
}
