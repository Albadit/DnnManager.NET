using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The Markdown of DNN Manager's release notes as a <see cref="FlowDocument"/>: headings (<c>#</c> to <c>###</c>),
/// bullet lists (nested by indentation), paragraphs, and in the text <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c> and
/// <c>[links](…)</c>. Only what the notes use - not a full Markdown reader.
/// </summary>
internal static partial class MarkdownDocument
{
    /// <summary>Adds <paramref name="markdown"/> to <paramref name="document"/>; <paramref name="link"/> says where a link goes.</summary>
    public static void Append(FlowDocument document, string markdown, Func<string, string> link)
    {
        // The open lists, outermost first - a bullet indented two more spaces is one level deeper.
        var lists = new List<List>();
        Paragraph? paragraph = null;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                paragraph = null;
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                lists.Clear();
                var level = heading.Groups[1].Length;
                paragraph = new Paragraph
                {
                    FontSize = level switch { 1 => 22, 2 => 17, _ => 14.5 },
                    FontWeight = FontWeights.SemiBold,
                    Margin = level switch
                    {
                        1 => new Thickness(0, document.Blocks.Count == 0 ? 0 : 28, 0, 6),
                        2 => new Thickness(0, 18, 0, 6),
                        _ => new Thickness(0, 12, 0, 4)
                    }
                };
                AddInlines(paragraph.Inlines, heading.Groups[2].Value, link);
                document.Blocks.Add(paragraph);
                paragraph = null;
                continue;
            }

            if (Bullet().Match(line) is { Success: true } bullet)
            {
                var depth = bullet.Groups[1].Value.Length / 2;
                while (lists.Count > depth + 1) lists.RemoveAt(lists.Count - 1);
                if (lists.Count < depth + 1)
                {
                    var list = new List { MarkerStyle = lists.Count == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle, Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(20, 0, 0, 0) };
                    if (lists.Count > 0 && lists[^1].ListItems.LastListItem is { } parent) parent.Blocks.Add(list);
                    else document.Blocks.Add(list);
                    lists.Add(list);
                }
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                AddInlines(paragraph.Inlines, bullet.Groups[2].Value, link);
                lists[^1].ListItems.Add(new ListItem(paragraph));
                continue;
            }

            // The next line of the paragraph or bullet above, or a paragraph of its own.
            if (paragraph is not null)
            {
                paragraph.Inlines.Add(new Run(" "));
                AddInlines(paragraph.Inlines, line.Trim(), link);
                continue;
            }
            lists.Clear();
            paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(paragraph.Inlines, line.Trim(), link);
            document.Blocks.Add(paragraph);
        }
    }

    private static void AddInlines(InlineCollection inlines, string text, Func<string, string> link)
    {
        var at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["bold"].Value, link);
                inlines.Add(bold);
            }
            else if (m.Groups["code"].Success)
            {
                inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 });
            }
            else if (m.Groups["text"].Success)
            {
                var target = link(m.Groups["url"].Value);
                var hyperlink = new Hyperlink(new Run(m.Groups["text"].Value)) { ToolTip = target };
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "LinkFg");
                hyperlink.Click += (_, _) => Shell.Open(target);
                inlines.Add(hyperlink);
            }
            else
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups["italic"].Value, link);
                inlines.Add(italic);
            }
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*]\s+(.+)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)|\*(?<italic>[^*\s][^*]*)\*")]
    private static partial Regex Inline();
}
