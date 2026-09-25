using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The activity log as a read-only rich-text box, so its text can be selected and copied (across
/// lines too) - each <see cref="LogEntry"/> is one coloured paragraph.
/// </summary>
public sealed class LogView : RichTextBox
{
    public LogView()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        // Keep a selection visible after clicking elsewhere (e.g. the Copy button).
        IsInactiveSelectionHighlightEnabled = true;
        IsUndoEnabled = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        Document = new FlowDocument { PagePadding = new Thickness(8, 6, 8, 6) };
        // A FlowDocument has its own default font - use the box's (set in XAML) instead.
        Document.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        Document.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
    }

    /// <summary>Shows <paramref name="entries"/> and follows every change to them.</summary>
    public void Attach(ObservableCollection<LogEntry> entries)
    {
        foreach (var entry in entries) Append(entry);
        entries.CollectionChanged += OnEntriesChanged;
        ScrollToEnd();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                // Follow new lines only while already at the bottom, so scrolling up to read or
                // select something isn't yanked away by the next line.
                var follow = IsAtEnd();
                foreach (LogEntry entry in e.NewItems) Append(entry);
                if (follow) ScrollToEnd();
                break;
            case NotifyCollectionChangedAction.Reset:
                Document.Blocks.Clear();
                break;
        }
    }

    private void Append(LogEntry entry)
    {
        var run = new Run(entry.Display);
        var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
        ApplyKindStyle(paragraph, entry.Kind);
        Document.Blocks.Add(paragraph);

        // Progress lines (e.g. a download percentage) are rewritten in place.
        if (entry.Kind == LogKind.Progress)
            entry.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LogEntry.Display)) run.Text = entry.Display;
            };
    }

    private static void ApplyKindStyle(Paragraph p, LogKind kind)
    {
        // Resource references (not fixed brushes) so the lines follow a theme switch.
        switch (kind)
        {
            case LogKind.Header:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogHeaderFg");
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, 8, 0, 0);
                break;
            case LogKind.Step:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogStep");
                p.Margin = new Thickness(0, 4, 0, 0);
                break;
            case LogKind.Success:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogSuccess");
                break;
            case LogKind.Fail:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogFail");
                break;
            case LogKind.Warning:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogWarn");
                p.FontWeight = FontWeights.SemiBold;
                break;
            case LogKind.Progress:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogProgress");
                break;
            default:
                p.SetResourceReference(TextElement.ForegroundProperty, "LogFg");
                break;
        }
    }

    private bool IsAtEnd() => VerticalOffset + ViewportHeight >= ExtentHeight - 4;
}
