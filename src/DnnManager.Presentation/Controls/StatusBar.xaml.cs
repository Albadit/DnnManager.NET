using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The bar along the bottom of the window: this PC's memory, CPU and disk use and the app's version (the running
/// operation is a toast - <see cref="OperationToast"/>). The figures are the <see cref="ServerStore"/>'s - they
/// arrive there every two seconds (the disk every ten; not while the window is minimized - see
/// <see cref="EfficiencyMode"/>) and only this bar hears about them.
/// </summary>
public partial class StatusBar : UserControl
{
    private ServerStore _store = null!;

    public StatusBar() => InitializeComponent();

    public void Attach(ServerStore store, string version)
    {
        _store = store;
        VersionText.Text = version;
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
        CpuText.Text = r.CpuPercent is { } cpu ? $"CPU {cpu:0.00}%" : "CPU -";
        CpuText.ToolTip = $"Processor use of this PC ({Environment.ProcessorCount} logical processors)";
        DiskText.Text = r.DiskRoot is null ? ""
            : $"Disk: {ByteSize.Format(r.DiskUsedBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        DiskText.ToolTip = r.DiskRoot is null ? null
            : $"{r.DiskRoot} (the projects folder's drive): {ByteSize.Format(r.DiskUsedBytes)} of {ByteSize.Format(r.DiskTotalBytes)} used, " +
              $"{ByteSize.Format(r.DiskTotalBytes - r.DiskUsedBytes)} free";
    }

}
