using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Saving resources while the window can't be seen - always on, not a setting. A window that can't be seen - minimized, closed
/// while DNN Manager keeps running in the background, or covered completely by other windows, on another virtual
/// desktop or behind a locked screen (<see cref="WindowOcclusion"/>,
/// as Chromium apps such as Docker Desktop decide it) - shows nothing, but WPF keeps running whatever animates in it at
/// about 60 frames a second, and the app keeps reading what only the window shows - so while it can't be seen that
/// stops, and once nothing runs either, the app goes into Windows' efficiency mode (EcoQoS and Idle priority - Task
/// Manager shows its leaf).
/// <para>
/// <b>Paused while it can't be seen.</b> Each part of the window follows <see cref="IsSavingProperty"/>, which the window
/// passes down to everything in it: the progress bars of the status bar, the rows and a site's overview, a changing
/// site's pulsing dot, redrawing a terminal (its output is still read and kept), following a log file (read on at
/// once when restored - no line is lost) and a toast's time to go away (it starts when the window is back). The
/// monitor stops reading this PC's figures and holds back folder-size walks (<see cref="ServerStore.SetSaving"/>).
/// <b>Not paused:</b> what Windows reports about IIS and the projects folder, the sites' reconciliation every
/// 30 seconds, operations with their progress and log lines, and the day's log file.
/// </para>
/// <para>
/// <b>Efficiency mode</b> (<see cref="PowerThrottling"/>) once the window hasn't been seen for a second, no operation
/// runs or ended in the last 5 seconds, and no terminal printed anything for 10 seconds: it makes CPU-bound work
/// slower, and a busy shell's output has to be read at full speed or the shell stalls. It ends at once when the window
/// can be seen again (restored, uncovered, clicked), an operation starts or a terminal prints - before anything can be
/// opened from the window, so nothing DNN Manager opens inherits the Idle priority. I/O and memory priority, the working set and the garbage
/// collector are never touched.
/// </para>
/// <para>
/// <b>Seen again</b>, everything is brought up to date at once, without a loading screen: Windows decides the app's speed
/// again first, the figures are read within about a second, a log reads what was written meanwhile, a terminal draws
/// once, and the Projects page reads what it shows (it follows the window itself).
/// </para>
/// </summary>
public sealed class EfficiencyMode
{
    // How long the window has to be out of sight before efficiency mode - a moment, so a quick Alt+Tab doesn't toggle it.
    private static readonly TimeSpan HiddenFor = TimeSpan.FromSeconds(1);
    // How long ago an operation has to have ended (what it changed is still being read back) before efficiency mode.
    private static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(5);
    // How long no terminal may have printed before EcoQoS.
    private static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(10);
    private const long Never = long.MinValue;

    /// <summary>
    /// Set on the main window while resources are saved, and inherited by everything in it - so a part of the window
    /// (a style's trigger, a control's code) follows it without being told: <c>(s:EfficiencyMode.IsSaving)</c>.
    /// </summary>
    public static readonly DependencyProperty IsSavingProperty = DependencyProperty.RegisterAttached("IsSaving", typeof(bool),
        typeof(EfficiencyMode), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsSaving(DependencyObject element) => (bool)element.GetValue(IsSavingProperty);

    public static void SetIsSaving(DependencyObject element, bool value) => element.SetValue(IsSavingProperty, value);

    /// <summary>
    /// For a trigger that starts an endless animation: true while its condition (the first value) holds and the
    /// element can be seen - it is on screen (its IsVisible, the second) and the window isn't minimized (its
    /// <see cref="IsSavingProperty"/>, the third). One binding, so the animation is started once: a MultiDataTrigger
    /// starts it once per condition, and each copy it replaces goes on ticking, unseen, until it is garbage collected.
    /// </summary>
    public static IMultiValueConverter WhileSeen { get; } = new WhileSeenConverter();

    private sealed class WhileSeenConverter : IMultiValueConverter
    {
        // The condition, seen, not saving - and, when given, animations not off (Motion).
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, true, false] or [true, true, false, false];

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private readonly OperationRunner _runner;
    private readonly ServerStore _store;
    private readonly ILogger<EfficiencyMode> _logger;
    // Looks again when EcoQoS may start - one tick per wait, not a polling timer.
    private readonly DispatcherTimer _check;
    private Window? _window;
    private WindowOcclusion? _occlusion;
    // On the clock of Now: since when resources are saved, when the last operation ended, when a terminal last printed.
    private long _savingSince, _operationEnded = Never, _lastOutput = Never;
    private bool _eco, _ecoRefused;

    public EfficiencyMode(OperationRunner runner, ServerStore store, ILogger<EfficiencyMode> logger)
    {
        _runner = runner; _store = store; _logger = logger;
        _check = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher);
        _check.Tick += (_, _) =>
        {
            _check.Stop();
            Update();
        };
        _runner.PropertyChanged += OnRunnerChanged;
    }

    /// <summary>The window can't be seen (minimized, hidden or covered): what only the window shows is paused.</summary>
    public bool IsSaving { get; private set; }

    /// <summary><see cref="IsSaving"/> changed. Raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Follows <paramref name="window"/> - the main window - from now on: minimized or not.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.StateChanged += (_, _) => Update();
        window.IsVisibleChanged += (_, _) => Update();
        _occlusion = new WindowOcclusion(window);
        _occlusion.Changed += (_, _) => Update();
        window.Closed += (_, _) => _occlusion.Dispose();
        Update();
    }

    /// <summary>
    /// A terminal printed something (UI thread, once per batch of output): no EcoQoS for the next 10 seconds, and
    /// none from now if it was on - the shell's output has to be read at full speed.
    /// </summary>
    public void NoteTerminalOutput()
    {
        _lastOutput = Now;
        // Not on: the wait that may be running looks at the time of the last output when it ends - nothing to do now.
        if (_eco) Update();
    }

    // ─── Following the window, the operations and the terminals ───────────

    private static long Now => Environment.TickCount64;

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        if (!_runner.IsBusy) _operationEnded = Now;
        Update();
    }

    /// <summary>Brings everything in line with the window's state, the setting, the operation and the terminals - now.</summary>
    private void Update()
    {
        // Hidden: closed while DNN Manager keeps running (not yet shown, at the start, doesn't count).
        var saving = _window is not null &&
                     (_window.WindowState == WindowState.Minimized || _window is { IsLoaded: true, IsVisible: false } ||
                      _occlusion?.IsOccluded == true);
        if (saving != IsSaving)
        {
            IsSaving = saving;
            _savingSince = Now;
            // Restored: Windows decides the app's speed again before anything is brought up to date.
            if (!saving) SetEcoQoS(false);
            _store.SetSaving(saving);
            SetIsSaving(_window!, saving);
            _logger.LogDebug("Saving resources while minimized: {Saving}", saving);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _check.Stop();
        if (EcoQoSFrom() is not { } from)
        {
            SetEcoQoS(false);
            return;
        }
        var now = Now;
        if (now >= from)
        {
            SetEcoQoS(true);
            return;
        }
        SetEcoQoS(false);
        _check.Interval = TimeSpan.FromMilliseconds(from - now);
        _check.Start();
    }

    // ─── EcoQoS ───────────────────────────────────────────────────────────

    /// <summary>When EcoQoS may start, on the clock of <see cref="Now"/>; null while it may not at all.</summary>
    private long? EcoQoSFrom()
    {
        if (!IsSaving || _runner.IsBusy || _ecoRefused) return null;
        var from = _savingSince + (long)HiddenFor.TotalMilliseconds;
        if (_operationEnded != Never) from = Math.Max(from, _operationEnded + (long)SettleFor.TotalMilliseconds);
        if (_lastOutput != Never) from = Math.Max(from, _lastOutput + (long)QuietFor.TotalMilliseconds);
        return from;
    }

    private void SetEcoQoS(bool on)
    {
        if (on == _eco) return;
        if (on)
        {
            _eco = PowerThrottling.TryEnter();
            // This Windows hasn't got it: not asked again.
            _ecoRefused = !_eco;
            if (_ecoRefused) _logger.LogInformation("Windows refused EcoQoS (error {Error}) - the app runs as usual while minimized.", PowerThrottling.LastError);
            return;
        }
        // Should Windows refuse, it is tried again with the next change.
        _eco = !PowerThrottling.Reset();
        if (_eco) _logger.LogWarning("Could not end EcoQoS (error {Error}).", PowerThrottling.LastError);
    }
}
