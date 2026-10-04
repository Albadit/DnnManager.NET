using System.Windows.Controls;
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
        CpuText.ToolTip = $"Processor use of this PC ({Environment.ProcessorCount} logical processors)";
        _store.SystemStatsChanged += (_, _) => ShowResources();
    }

    // A sample without the CPU use - the first one, and the first after a pause - is skipped, so the three figures
    // appear and change together; until the first full sample the bar shows the XAML's placeholders.
    private void ShowResources()
    {
        if (_store.SystemStats is not { CpuPercent: { } cpu } r) return;
        RamText.Text = r.MemoryTotalBytes == 0 ? "RAM 0.00 GB" : $"RAM {ByteSize.Format(r.MemoryUsedBytes)}";
        RamWidest.Text = r.MemoryTotalBytes == 0 ? "" : $"RAM {ByteSize.Format(r.MemoryTotalBytes)}";
        RamText.ToolTip = r.MemoryTotalBytes == 0 ? null
            : $"Memory in use: {ByteSize.Format(r.MemoryUsedBytes)} of {ByteSize.Format(r.MemoryTotalBytes)} ({100d * r.MemoryUsedBytes / r.MemoryTotalBytes:0}%)";
        CpuText.Text = $"CPU {cpu:0.00}%";
        DiskText.Text = r.DiskRoot is null ? "Disk: --.-- GB used (limit --.-- GB)"
            : $"Disk: {ByteSize.Format(r.DiskUsedBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        DiskWidest.Text = r.DiskRoot is null ? ""
            : $"Disk: {ByteSize.Format(r.DiskTotalBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        DiskText.ToolTip = r.DiskRoot is null ? null
            : $"{r.DiskRoot} (the projects folder's drive): {ByteSize.Format(r.DiskUsedBytes)} of {ByteSize.Format(r.DiskTotalBytes)} used, " +
              $"{ByteSize.Format(r.DiskTotalBytes - r.DiskUsedBytes)} free";
    }

}
