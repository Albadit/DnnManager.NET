using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The Output tab's log: every run grouped by stage - a heading with its duration, then its lines, flush with it -
/// with warnings and errors in boxes and a result bar at the end of each run; lines outside a run stand on their own.
/// A read-only rich-text box, so text can be selected and copied across lines, and searched (Ctrl+F). Follows
/// <see cref="ActivityLog"/> as it changes, without building anything twice.
/// </summary>
public sealed partial class PipelineLog : RichTextBox, ISearchTarget
{
    // The width of a stage heading's duration ("3.1s · failed").
    private const double DurationWidth = 150;
    // Everything is this far in, so a box's background can stick out to the left while its text stays in line.
    private const double Inset = 12;

    private readonly Dictionary<OutputItem, Block> _items = new();
    private readonly Dictionary<OutputRun, RunView> _runs = new();
    private readonly Dictionary<Block, RunView> _runBlocks = new();
    private ActivityLog? _log;
    private Regex _highlights = HighlightsFor("");

    public PipelineLog()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        // Keep a selection visible after clicking elsewhere.
        IsInactiveSelectionHighlightEnabled = true;
        IsUndoEnabled = false;
        // Links (a site's address) open with a click; the result bar's too.
        IsDocumentEnabled = true;
        // Always there (empty while nothing scrolls): a scrollbar that only comes when needed doesn't make the page
        // narrower - the text would run under it.
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        Document = new FlowDocument
        {
            PagePadding = new Thickness(22 - Inset, 12, 22, 12),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        // A FlowDocument has its own default font - use the box's instead.
        Document.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        Document.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        Document.LineHeight = FontSize * 1.6;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty && Document is { } document) document.LineHeight = FontSize * 1.6;
    }

    /// <summary>Shows <paramref name="log"/> and follows every change to it.</summary>
    /// <param name="hostSuffix">Host names ending in it (<c>mysite.dnndev.me</c>) are picked out like other values.</param>
    internal void Attach(ActivityLog log, string hostSuffix)
    {
        _log = log;
        _highlights = HighlightsFor(hostSuffix);
        foreach (var item in log.Items) Add(item, Document.Blocks.Count);
        log.Items.CollectionChanged += OnItemsChanged;
        Arrange();
        ScrollToEnd();
    }

    /// <summary>The hostname suffix changed in the settings.</summary>
    internal void SetHostSuffix(string hostSuffix) => _highlights = HighlightsFor(hostSuffix);

    /// <summary>Time went on: the running stage's heading shows its duration so far.</summary>
    internal void Tick()
    {
        foreach (var run in _runs.Values) run.Tick();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Change(() =>
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                var at = e.NewStartingIndex;
                foreach (OutputItem item in e.NewItems) Add(item, at++);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (OutputItem item in e.OldItems) Remove(item);
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var item in _items.Keys.ToList()) Remove(item);
                Document.Blocks.Clear();
                break;
        }
        Arrange();
    });

    private void Add(OutputItem item, int index)
    {
        Block block;
        if (item is OutputRun run)
        {
            var view = new RunView(this, run);
            _runs[run] = view;
            block = view.Section;
            _runBlocks[block] = view;
        }
        else
        {
            var line = (OutputLine)item;
            block = MakeLine(line);
            if (line.Level == LineLevel.Progress) WatchProgress(line, () => block);
        }
        _items[item] = block;
        if (index >= Document.Blocks.Count) Document.Blocks.Add(block);
        else Document.Blocks.InsertBefore(Document.Blocks.ElementAt(index), block);
    }

    private void Remove(OutputItem item)
    {
        if (!_items.Remove(item, out var block)) return;
        Document.Blocks.Remove(block);
        _runBlocks.Remove(block);
        if (item is OutputRun run && _runs.Remove(run, out var view)) view.Detach();
    }

    /// <summary>
    /// After the blocks changed: a run that isn't the first gets its title above it (the header names the newest
    /// one), and the first stage heading isn't pushed down.
    /// </summary>
    private void Arrange()
    {
        var first = true;
        // A copy: a title going in changes the document.
        foreach (var block in Document.Blocks.ToList())
        {
            if (_runBlocks.TryGetValue(block, out var view))
            {
                view.ShowTitle(!first);
                view.SpaceStages(first);
            }
            first = false;
        }
    }

    /// <summary>Changes the document: while the end was in view it stays in view - scrolled up, it stays put.</summary>
    private void Change(Action change)
    {
        var follow = IsAtEnd();
        change();
        // Later, not now: scrolling lays the window out, in the middle of a change others (the stage list) haven't
        // heard of yet - they would show it twice.
        if (follow && !_scrollPending)
        {
            _scrollPending = true;
            Dispatcher.InvokeAsync(() =>
            {
                _scrollPending = false;
                ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _scrollPending;

    private bool IsAtEnd() => VerticalOffset + ViewportHeight >= ExtentHeight - 4;

    // ─── A run ────────────────────────────────────────────────────────────

    /// <summary>A run in the document: its title (when it isn't first), its stages, its closing notes and its result.</summary>
    private sealed class RunView
    {
        private readonly PipelineLog _log;
        private readonly OutputRun _run;
        private readonly Dictionary<OutputStage, StageView> _stages = new();
        private readonly Section _notes = new();
        private readonly Dictionary<OutputLine, Block> _noteBlocks = new();
        private Paragraph? _title;
        private Block? _result;

        public Section Section { get; } = new();

        public RunView(PipelineLog log, OutputRun run)
        {
            _log = log;
            _run = run;
            Section.Blocks.Add(_notes);
            foreach (var stage in run.Stages) Watch(stage);
            foreach (var line in run.Notes) AddNote(line);
            run.Stages.CollectionChanged += OnStagesChanged;
            run.Notes.CollectionChanged += OnNotesChanged;
            run.PropertyChanged += OnRunChanged;
            if (!run.IsRunning) ShowResult();
        }

        public void Detach()
        {
            _run.Stages.CollectionChanged -= OnStagesChanged;
            _run.Notes.CollectionChanged -= OnNotesChanged;
            _run.PropertyChanged -= OnRunChanged;
            foreach (var stage in _stages.Keys) stage.PropertyChanged -= OnStageChanged;
            foreach (var view in _stages.Values) view.Detach();
        }

        public void Tick()
        {
            if (_run.Current is { } current && _stages.TryGetValue(current, out var view)) view.UpdateHeading();
        }

        /// <summary>The run's title above it - for every run but the first in the log.</summary>
        public void ShowTitle(bool show)
        {
            if (show && _title is null)
            {
                _title = new Paragraph { Margin = new Thickness(Inset, 22, 0, 6), Padding = new Thickness(0, 10, 0, 0),
                    BorderThickness = new Thickness(0, 1, 0, 0) };
                _title.SetResourceReference(Block.BorderBrushProperty, "OutBorder");
                _title.Inlines.Add(Styled(_run.Title, "OutMuted", bold: true));
                _title.Inlines.Add(Styled($"   {_run.StartedAt:HH:mm:ss}", "OutTime"));
                if (Section.Blocks.FirstBlock is { } first) Section.Blocks.InsertBefore(first, _title);
                else Section.Blocks.Add(_title);
            }
            else if (!show && _title is not null)
            {
                Section.Blocks.Remove(_title);
                _title = null;
            }
        }

        /// <summary>Spaces the stage headings: none above the very first in the log.</summary>
        public void SpaceStages(bool firstInLog)
        {
            var first = firstInLog && _title is null;
            foreach (var stage in _run.Stages)
            {
                if (!_stages.TryGetValue(stage, out var view) || !view.IsShown) continue;
                view.SetTopSpace(first ? 0 : stage.Status == StageStatus.Skipped ? 4 : 8);
                first = false;
            }
        }

        private void OnStagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _log.Change(() =>
        {
            if (e.NewItems is not null)
                foreach (OutputStage stage in e.NewItems) Watch(stage);
            _log.Arrange();
        });

        private void Watch(OutputStage stage)
        {
            if (_stages.ContainsKey(stage)) return;
            _stages[stage] = new StageView(_log, stage);
            stage.PropertyChanged += OnStageChanged;
            Place(stage);
        }

        private void OnStageChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not OutputStage stage || !_stages.TryGetValue(stage, out var view)) return;
            if (e.PropertyName is nameof(OutputStage.Status) or nameof(OutputStage.Title))
                _log.Change(() =>
                {
                    Place(stage);
                    view.UpdateHeading();
                    _log.Arrange();
                });
            else if (e.PropertyName == nameof(OutputStage.DurationText))
                view.UpdateHeading();
        }

        /// <summary>A stage that has run (or was skipped) goes in among the others, in their order; one still to come isn't shown.</summary>
        private void Place(OutputStage stage)
        {
            var view = _stages[stage];
            if (!stage.IsShownInLog || view.IsShown) return;
            var next = _run.Stages.Skip(_run.Stages.IndexOf(stage) + 1)
                .Select(s => _stages.GetValueOrDefault(s)).FirstOrDefault(v => v is { IsShown: true });
            Section.Blocks.InsertBefore(next?.Section ?? (Block)_notes, view.Section);
            view.IsShown = true;
        }

        private void OnNotesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _log.Change(() =>
        {
            if (e.NewItems is not null)
                foreach (OutputLine line in e.NewItems) AddNote(line);
            if (e.OldItems is not null)
                foreach (OutputLine line in e.OldItems)
                    if (_noteBlocks.Remove(line, out var block)) _notes.Blocks.Remove(block);
        });

        private void AddNote(OutputLine line)
        {
            var block = _log.MakeLine(line);
            _noteBlocks[line] = block;
            _notes.Blocks.Add(block);
            if (line.Level == LineLevel.Progress) _log.WatchProgress(line, () => _noteBlocks.GetValueOrDefault(line));
        }

        private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(OutputRun.Status) or nameof(OutputRun.Link) && !_run.IsRunning)
                _log.Change(ShowResult);
        }

        private void ShowResult()
        {
            if (_result is not null) Section.Blocks.Remove(_result);
            _result = _log.MakeResult(_run);
            Section.Blocks.Add(_result);
        }
    }

    // ─── A stage ──────────────────────────────────────────────────────────

    /// <summary>A stage in the document: its heading - title, and duration on the right - and its lines.</summary>
    private sealed class StageView
    {
        private readonly PipelineLog _log;
        private readonly OutputStage _stage;
        private readonly Dictionary<OutputLine, Block> _lines = new();
        private readonly Paragraph _heading;
        private readonly Run _title;
        private readonly Run _duration;

        public Section Section { get; } = new();

        /// <summary>It is in the document (it has run, or was skipped).</summary>
        public bool IsShown { get; set; }

        public StageView(PipelineLog log, OutputStage stage)
        {
            _log = log;
            _stage = stage;
            _title = new Run(stage.Title);
            _duration = new Run();
            // The title, with its duration floating on the right of the same line - a floater is placed where it is
            // anchored, so it goes first.
            _heading = new Paragraph { Margin = new Thickness(Inset, 8, 0, 0) };
            _heading.Inlines.Add(new Floater(new Paragraph(_duration) { TextAlignment = TextAlignment.Right, Margin = new Thickness(0) })
            {
                HorizontalAlignment = HorizontalAlignment.Right, Width = DurationWidth, Padding = new Thickness(0), Margin = new Thickness(0)
            });
            _heading.Inlines.Add(_title);
            Section.Blocks.Add(_heading);
            foreach (var line in stage.Lines) AddLine(line);
            stage.Lines.CollectionChanged += OnLinesChanged;
            UpdateHeading();
        }

        public void Detach() => _stage.Lines.CollectionChanged -= OnLinesChanged;

        public void SetTopSpace(double space) => _heading.Margin = new Thickness(Inset, space, 0, 0);

        /// <summary>The heading's colours and duration, as the stage stands now.</summary>
        public void UpdateHeading()
        {
            _title.Text = _stage.Title;
            var (title, time) = _stage.Status switch
            {
                StageStatus.Warning => ("OutWarnText", "OutWarn"),
                StageStatus.Failed => ("OutErrorText", "OutErrorSoft"),
                StageStatus.Skipped or StageStatus.Cancelled => ("OutDim", "OutDim"),
                _ => ("OutHeading", "OutHeadingTime")
            };
            _title.SetResourceReference(TextElement.ForegroundProperty, title);
            _duration.SetResourceReference(TextElement.ForegroundProperty, time);
            _duration.Text = _stage.Status == StageStatus.Failed ? $"{_stage.DurationText} · failed" : _stage.DurationText;
        }

        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _log.Change(() =>
        {
            if (e.NewItems is not null)
                foreach (OutputLine line in e.NewItems) AddLine(line);
            if (e.OldItems is not null)
                foreach (OutputLine line in e.OldItems)
                    if (_lines.Remove(line, out var block)) Section.Blocks.Remove(block);
        });

        private void AddLine(OutputLine line)
        {
            var block = _log.MakeLine(line);
            _lines[line] = block;
            Section.Blocks.Add(block);
            if (line.Level == LineLevel.Progress) _log.WatchProgress(line, () => _lines.GetValueOrDefault(line));
        }
    }

    // ─── Lines ────────────────────────────────────────────────────────────

    /// <summary>A line as a block: plain, a warning box or an error box - flush with the stage title, no bullet.</summary>
    private Block MakeLine(OutputLine line) => line.Level switch
    {
        LineLevel.Warn => MakeWarning(line),
        LineLevel.Error => MakeError(line),
        _ => MakePlain(line)
    };

    private Paragraph MakePlain(OutputLine line)
    {
        var p = new Paragraph { Margin = new Thickness(Inset, 0, 0, 2) };
        FillPlain(p, line);
        return p;
    }

    private void FillPlain(Paragraph p, OutputLine line)
    {
        p.Inlines.Clear();
        p.Inlines.Add(Stamp(line));
        if (line.Level == LineLevel.Progress && ProgressPattern().Match(line.Text) is { Success: true } m &&
            long.TryParse(m.Groups[1].Value, out var done) && long.TryParse(m.Groups[2].Value, out var total) && total > 0)
        {
            // "1234/4487  App_Code\x.cs": how far, as a bar - and the file it is at.
            p.Inlines.Add(Styled($"{done:N0} / {total:N0}", "OutText"));
            p.Inlines.Add(new Run("  "));
            p.Inlines.Add(new InlineUIContainer(Bar((double)done / total)) { BaselineAlignment = BaselineAlignment.Center });
            p.Inlines.Add(new Run("  "));
            AddText(p.Inlines, m.Groups[3].Value, "OutMuted", LineLevel.Info);
            return;
        }
        // A success reads as any other line - the stage's tick says it went well; only the end of a progress line
        // ("Copied 4 487 files") stands out.
        // "Database seeded." - a line, not a sentence.
        var text = line.Text.EndsWith('.') && !line.Text.EndsWith("..") ? line.Text[..^1] : line.Text;
        AddText(p.Inlines, text, line.Completes ? "OutSuccessText" : line.Level == LineLevel.Progress ? "OutMuted" : "OutText",
            line.Completes ? LineLevel.Success : LineLevel.Info);
    }

    private Paragraph MakeWarning(OutputLine line)
    {
        // A line like the others - its badge and colour say it is a warning.
        var p = new Paragraph { Margin = new Thickness(Inset, 0, 0, 2) };
        p.Inlines.Add(Stamp(line));
        p.Inlines.Add(Badge("WARN", "OutWarn"));
        p.Inlines.Add(new Run("  "));
        AddText(p.Inlines, line.Text, "OutWarnText", LineLevel.Warn);
        return p;
    }

    private Section MakeError(OutputLine line)
    {
        // Lines like the others - the badge and colour say it is an error; what lies behind it and what to do under it.
        var box = new Section { Margin = new Thickness(Inset, 0, 0, 2) };

        var first = new Paragraph { Margin = new Thickness(0) };
        first.Inlines.Add(Stamp(line));
        first.Inlines.Add(Badge("ERROR", "OutError"));
        first.Inlines.Add(new Run("  "));
        AddText(first.Inlines, line.Text, "OutErrorText", LineLevel.Error);
        box.Blocks.Add(first);

        // Under the message, not under the time.
        var indent = MessageIndent("ERROR");
        foreach (var detail in line.Details)
        {
            var p = new Paragraph { Margin = new Thickness(indent, 0, 0, 0) };
            AddText(p.Inlines, detail, "OutErrorDetail", LineLevel.Error);
            box.Blocks.Add(p);
        }
        if (line.Hint is { } hint)
        {
            var p = new Paragraph { Margin = new Thickness(indent, 2, 0, 0) };
            p.Inlines.Add(Styled("→ ", "OutErrorSoft"));
            AddText(p.Inlines, hint, "OutHeading", LineLevel.Info);
            box.Blocks.Add(p);
        }
        return box;
    }

    /// <summary>A progress line is rewritten in place: its block is filled again.</summary>
    private void WatchProgress(OutputLine line, Func<Block?> block) =>
        line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputLine.Text) && block() is Paragraph p) Change(() => FillPlain(p, line));
        };

    // ─── The end of a run ─────────────────────────────────────────────────

    /// <summary>
    /// The end of a run, as a line of its own like the others: SUCCESS with what the operation says it did ("Removal
    /// complete") and where to open the result; ERROR with where it stopped; CANCELLED with what was put back - each with
    /// how long it took.
    /// </summary>
    private Paragraph MakeResult(OutputRun run)
    {
        var p = new Paragraph { Margin = new Thickness(Inset, 6, 0, 16) };
        p.Inlines.Add(Stamp(run.EndedAt ?? DateTime.Now));
        var (badge, badgeBrush, text, textBrush) = run.Status switch
        {
            RunStatus.Finished => ("SUCCESS", "OutOk", run.ClosingTitle ?? $"{run.Title} finished", "OutText"),
            RunStatus.Cancelled => ("CANCELLED", "OutMuted", run.Outcome ?? $"{run.Title} was cancelled", "OutText"),
            _ => ("ERROR", "OutError", run.StoppedAt is { } at ? $"{run.Title} stopped at {at.Name}" : $"{run.Title} failed", "OutErrorText")
        };
        p.Inlines.Add(Badge(badge, badgeBrush));
        p.Inlines.Add(new Run("  "));
        AddText(p.Inlines, text, textBrush, LineLevel.Warn);

        var more = new List<string> { run.DurationText };
        if (run.Status == RunStatus.Failed && run.Skipped > 0) more.Add(run.Skipped == 1 ? "1 stage skipped" : $"{run.Skipped} stages skipped");
        else if (run.Status == RunStatus.Finished && run.IssueText is { } issues) more.Add(issues);
        p.Inlines.Add(Styled("  · " + string.Join(" · ", more), "OutMuted"));

        if (run.Status == RunStatus.Finished && run.Link is { } url)
        {
            var link = new Hyperlink(new Run($"{url} ↗")) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Open in your browser" };
            link.SetResourceReference(TextElement.ForegroundProperty, "OutValue");
            link.Click += (_, _) => Shell.Open(url, quiet: true);
            p.Inlines.Add(new Run("  "));
            p.Inlines.Add(link);
        }
        return p;
    }

    // ─── Pieces ───────────────────────────────────────────────────────────

    /// <summary>"09:37:21.4" and the gap after it.</summary>
    private static Run Stamp(OutputLine line) => Stamp(line.Time);

    private static Run Stamp(DateTime time) => Styled(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ", "OutTime");

    private static Run Styled(string text, string brush, bool bold = false)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brush);
        if (bold) run.FontWeight = FontWeights.SemiBold;
        return run;
    }

    /// <summary>"WARN", "ERROR" - a small coloured label.</summary>
    private static InlineUIContainer Badge(string text, string brush)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "OutBadgeText");
        var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 0), Child = label };
        badge.SetResourceReference(Border.BackgroundProperty, brush);
        return new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>A thin bar, filled to <paramref name="fraction"/>.</summary>
    private static FrameworkElement Bar(double fraction)
    {
        var track = new Grid { Width = 150, Height = 3 };
        var back = new Border { CornerRadius = new CornerRadius(1.5) };
        back.SetResourceReference(Border.BackgroundProperty, "OutBorder");
        var fill = new Border { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
            Width = 150 * Math.Clamp(fraction, 0, 1) };
        fill.SetResourceReference(Border.BackgroundProperty, "OutActive");
        track.Children.Add(back);
        track.Children.Add(fill);
        return track;
    }

    /// <summary>How far a line's message starts in from the time - the time, a badge and the gaps.</summary>
    private double MessageIndent(string badge)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double Width(string text, Typeface typeface, double size) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
        return Width("00:00:00  ", face, FontSize) + Width(badge, bold, 11) + 12 + Width("  ", face, FontSize);
    }

    /// <summary>
    /// Adds <paramref name="text"/> in <paramref name="brush"/>, picking out what matters: addresses (as links),
    /// databases and servers in teal; file paths dimmed and shortened to under your profile; on a plain line, what
    /// is in brackets after it dimmed.
    /// </summary>
    private void AddText(InlineCollection inlines, string text, string brush, LineLevel level)
    {
        var at = 0;
        foreach (Match m in _highlights.Matches(text))
        {
            if (m.Index > at) inlines.Add(Styled(text[at..m.Index], brush));
            if (m.Groups["url"].Success)
            {
                var url = m.Value.TrimEnd('.', ',', ')');
                var link = new Hyperlink(new Run(url)) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Open in your browser" };
                link.SetResourceReference(TextElement.ForegroundProperty, "OutValue");
                link.Click += (_, _) => Shell.Open(url, quiet: true);
                inlines.Add(link);
                at = m.Index + url.Length;
                continue;
            }
            if (m.Groups["path"].Success)
            {
                var path = Styled(ShortPath(m.Value), "OutMuted");
                path.ToolTip = m.Value;
                inlines.Add(path);
            }
            else if (m.Groups["paren"].Success) inlines.Add(Styled(m.Value, level == LineLevel.Info ? "OutMuted" : brush));
            else inlines.Add(Styled(m.Value, "OutValue"));
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(Styled(text[at..], brush));
    }

    /// <summary>C:\Users\you\AppData\Local\Temp\x.bak - AppData\Local\Temp\x.bak.</summary>
    private static string ShortPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
        return path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? path[profile.Length..] : path;
    }

    /// <summary>
    /// What a line picks out: an address; a file path; a [database]; a server,port; a project's host name
    /// (mysite.dnndev.me); what is in brackets.
    /// </summary>
    private static Regex HighlightsFor(string hostSuffix) => new(
        @"(?<url>https?://[^\s'""<>]+)|(?<path>(?:[A-Za-z]:\\|\\\\)[^\s'""<>|]+)|(?<db>\[[^\[\]\s][^\[\]]*\])" +
        @"|(?<server>\b[A-Za-z][\w.-]*,\d{2,5}\b)" +
        (hostSuffix.Length > 0 ? $@"|(?<host>\b[\w-]+(?:\.[\w-]+)*\.{Regex.Escape(hostSuffix)}\b)" : "") +
        @"|(?<paren>\([^()]*\))", RegexOptions.IgnoreCase);

    // A file copy's progress: "1234/4487  App_Code\x.cs".
    [GeneratedRegex(@"^\s*(\d+)\s*/\s*(\d+)\s+(.*)$")]
    private static partial Regex ProgressPattern();

    // ─── Search (Ctrl+F) ─────────────────────────────────────────────────

    private List<TextRange> _matches = new();

    /// <summary>The log grew - a search looks again.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Highlights every match of <paramref name="query"/> in every stage; their number.</summary>
    public int Find(SearchQuery query)
    {
        ClearSearch();
        if (query.IsEmpty) return 0;
        // A line's text is searched whole (a highlight splits the runs it is in), and every match found before any is
        // highlighted - highlighting changes the document being read.
        foreach (var paragraph in Paragraphs(Document.Blocks))
        {
            foreach (var (at, length) in query.Matches(TextOf(paragraph)))
                if (PositionAt(paragraph, at) is { } start && PositionAt(paragraph, at + length) is { } end)
                    _matches.Add(new TextRange(start, end));
        }
        var highlight = FindResource("SearchMatchBg");
        foreach (var match in _matches) match.ApplyPropertyValue(TextElement.BackgroundProperty, highlight);
        return _matches.Count;
    }

    /// <summary>Every paragraph, also those in sections and table cells (a stage heading, an error box).</summary>
    private static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case Section s:
                    foreach (var p in Paragraphs(s.Blocks)) yield return p;
                    break;
                case Table t:
                    foreach (var p in Paragraphs(t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks)))
                        yield return p;
                    break;
            }
        }
    }

    private static string TextOf(Paragraph paragraph)
    {
        var text = new System.Text.StringBuilder();
        for (var p = paragraph.ContentStart; p is not null && p.CompareTo(paragraph.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text) text.Append(p.GetTextInRun(LogicalDirection.Forward));
        return text.ToString();
    }

    /// <summary>The position <paramref name="offset"/> characters into <paramref name="paragraph"/>'s text, across its runs.</summary>
    private static TextPointer? PositionAt(Paragraph paragraph, int offset)
    {
        for (var p = paragraph.ContentStart; p is not null && p.CompareTo(paragraph.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var length = p.GetTextRunLength(LogicalDirection.Forward);
            if (offset <= length) return p.GetPositionAtOffset(offset);
            offset -= length;
        }
        return null;
    }

    /// <summary>Selects match <paramref name="index"/> and scrolls it into view.</summary>
    public void ShowMatch(int index, bool reveal = true)
    {
        if (index < 0 || index >= _matches.Count) return;
        var match = _matches[index];
        Selection.Select(match.Start, match.End);
        if (reveal) match.Start.Paragraph?.BringIntoView();
    }

    public void ClearSearch()
    {
        foreach (var match in _matches) match.ApplyPropertyValue(TextElement.BackgroundProperty, null);
        _matches = new List<TextRange>();
        Selection.Select(Document.ContentEnd, Document.ContentEnd);
    }
}
