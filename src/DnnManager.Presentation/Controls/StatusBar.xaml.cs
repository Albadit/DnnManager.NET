using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The bar along the bottom of the window: this PC's memory, CPU and disk use, the running operation with Cancel,
/// the switch for the terminal panel and the app's version. The figures are the <see cref="ServerStore"/>'s - they
/// arrive there every two seconds (the disk every ten) and only this bar hears about them.
/// </summary>
public partial class StatusBar : UserControl
{
    private ServerStore _store = null!;
    private OperationRunner _runner = null!;

    public StatusBar() => InitializeComponent();

    /// <summary>The terminal button was clicked - the window shows or hides the terminal panel.</summary>
    public event EventHandler? ActivityToggled;

    /// <summary>The running operation was clicked - the window shows its activity log.</summary>
    public event EventHandler? OperationClicked;

    /// <summary>Whether the terminal panel is open, shown on its button.</summary>
    public bool IsActivityOpen
    {
        get => ActivityButton.IsChecked == true;
        set
        {
            ActivityButton.IsChecked = value;
            ActivityButton.ToolTip = value ? "Hide the terminal" : "Show the terminal (activity log and shells)";
        }
    }

    public void Attach(ServerStore store, OperationRunner runner, string version)
    {
        _store = store; _runner = runner;
        VersionText.Text = version;
        _runner.PropertyChanged += OnRunnerChanged;
        _store.SystemStatsChanged += (_, _) => ShowResources();
        Loaded += (_, _) => ReserveWidths();
    }

    /// <summary>
    /// Gives RAM and CPU the width of their widest value, so the figures after them stay put as the numbers change.
    /// (Disk is last, and its numbers hardly move.)
    /// </summary>
    private void ReserveWidths()
    {
        RamText.MinWidth = Measure(RamText, $"RAM {888.88:N2} GB");
        CpuText.MinWidth = Measure(CpuText, $"CPU {100:0.00}%");
    }

    private static double Measure(TextBlock block, string text) => Math.Ceiling(new FormattedText(text,
        CultureInfo.CurrentCulture, block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace);

    private void ShowResources()
    {
        if (_store.SystemStats is not { } r) return;
        RamText.Text = r.MemoryTotalBytes == 0 ? "" : $"RAM {ByteSize.Format(r.MemoryUsedBytes)}";
        RamText.ToolTip = r.MemoryTotalBytes == 0 ? null
            : $"Memory in use: {ByteSize.Format(r.MemoryUsedBytes)} of {ByteSize.Format(r.MemoryTotalBytes)} ({100d * r.MemoryUsedBytes / r.MemoryTotalBytes:0}%)";
        CpuText.Text = r.CpuPercent is { } cpu ? $"CPU {cpu:0.00}%" : "CPU …";
        CpuText.ToolTip = $"Processor use of this PC ({Environment.ProcessorCount} logical processors)";
        DiskText.Text = r.DiskRoot is null ? ""
            : $"Disk: {ByteSize.Format(r.DiskUsedBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        DiskText.ToolTip = r.DiskRoot is null ? null
            : $"{r.DiskRoot} (the projects folder's drive): {ByteSize.Format(r.DiskUsedBytes)} of {ByteSize.Format(r.DiskTotalBytes)} used, " +
              $"{ByteSize.Format(r.DiskTotalBytes - r.DiskUsedBytes)} free";
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        var busy = _runner.IsBusy;
        OperationPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OperationProgress.IsIndeterminate = busy;
        OperationText.Text = busy ? $"{_runner.Current}…" : "";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _runner.Cancel();

    private void Activity_Click(object sender, RoutedEventArgs e) => ActivityToggled?.Invoke(this, EventArgs.Empty);

    private void Operation_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OperationClicked?.Invoke(this, EventArgs.Empty);
}
