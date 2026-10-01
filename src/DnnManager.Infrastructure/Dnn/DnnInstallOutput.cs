using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>One step of DNN's install output, e.g. "Installing Package File DNNCE_HTML_10.03.03_Install" - and whether DNN marked it Success or Error!.</summary>
public sealed record DnnInstallStep(string Text, bool? Succeeded);

/// <summary>
/// DNN's install page as it streams in (<c>Install.aspx?mode=install</c>): one line per step, flushed as DNN goes, ending
/// in "Installation Complete". Turns it into readable steps, and decides how the install went - from what DNN wrote, not
/// from the HTTP status, which is 200 even when the install failed.
/// </summary>
public sealed partial class DnnInstallOutput
{
    /// <summary>Any of these means the install didn't fully succeed, even when "Installation Complete" follows.</summary>
    public static readonly IReadOnlyList<string> FailureMarkers = ["Error!", "Upgrade Error", "failed to install", "currently in progress"];

    private readonly StringBuilder _body = new();
    private readonly StringBuilder _pending = new();
    private readonly List<DnnInstallStep> _failedSteps = [];
    // The page header (styles, logo) comes before DNN's first <h1>; a comment around it hides part of it.
    private bool _started, _inComment;

    /// <summary>Everything DNN sent so far.</summary>
    public string Body => _body.ToString();

    /// <summary>Takes the next chunk of the page and returns the steps it completed.</summary>
    public IReadOnlyList<DnnInstallStep> Add(string chunk)
    {
        _body.Append(chunk);
        _pending.Append(chunk);
        var text = _pending.ToString();
        var steps = new List<DnnInstallStep>();
        var start = 0;
        foreach (Match delimiter in Delimiter().Matches(text))
        {
            var end = delimiter.Index + delimiter.Length;
            if (Parse(text[start..end]) is { } step) steps.Add(step);
            start = end;
        }
        _pending.Clear().Append(text[start..]);
        return steps;
    }

    /// <summary>What is left once DNN has closed the response.</summary>
    public IReadOnlyList<DnnInstallStep> Finish()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return Parse(rest) is { } step ? [step] : [];
    }

    /// <summary>
    /// Ok when DNN wrote "Installation Complete" and "Successfully Installed Site 0" and nothing it marks as an error;
    /// otherwise what went wrong, as DNN put it.
    /// </summary>
    public Result Outcome()
    {
        var html = Body;
        var markers = FailureMarkers.Where(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        var complete = html.Contains("Installation Complete", StringComparison.Ordinal);
        var site = html.Contains("Successfully Installed Site 0", StringComparison.Ordinal);
        if (complete && site && markers.Count == 0) return Result.Ok();

        if (html.Contains("Nothing To Install At This Time", StringComparison.Ordinal) ||
            html.Contains("current Database Version are identical", StringComparison.Ordinal))
            return Result.Fail("DNN is installed already - its database holds a DNN site.");
        if (markers.Contains("currently in progress"))
            return Result.Fail("Another installation of this site is running (DNN's installBlocker.lock).");
        if (html.Contains("Could not connect to database", StringComparison.OrdinalIgnoreCase))
            return Result.Fail("DNN couldn't connect to its database - check the site's connection string and that the site's identity may sign in.");

        var failed = _failedSteps.Select(s => s.Text).Take(3).ToList();
        var why = failed.Count > 0 ? string.Join("; ", failed)
            : !complete ? "it stopped before \"Installation Complete\""
            : !site ? "the website wasn't created"
            : $"it reported {string.Join(", ", markers)}";
        return Result.Fail($"DNN's installation didn't complete: {why}.");
    }

    private DnnInstallStep? Parse(string segment)
    {
        var s = segment;
        if (_inComment)
        {
            var close = s.IndexOf("-->", StringComparison.Ordinal);
            if (close < 0) return null;
            _inComment = false;
            s = s[(close + 3)..];
        }
        var open = s.IndexOf("<!--", StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = s.IndexOf("-->", open, StringComparison.Ordinal);
            if (close < 0)
            {
                _inComment = true;
                s = s[..open];
            }
            else
            {
                s = s[..open] + s[(close + 3)..];
            }
        }
        if (!_started)
        {
            if (s.IndexOf("<h1>", StringComparison.OrdinalIgnoreCase) < 0) return null;
            _started = true;
        }

        bool? succeeded = null;
        var status = StatusFont().Match(s);
        if (status.Success)
        {
            succeeded = status.Groups[2].Value.StartsWith("Success", StringComparison.OrdinalIgnoreCase);
            s = StatusFont().Replace(s, " ");
        }
        var text = Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(s, " ")), " ").Trim();
        var elapsed = Elapsed().Match(text);
        if (elapsed.Success) text = text[elapsed.Length..].Trim();
        text = text.TrimEnd(':').Trim();
        if (text.Length == 0 && succeeded is null) return null;
        if (text.StartsWith("Click Here To Access Your Site", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length > 300) text = text[..300] + "…";

        // "Site failed to install:Error!" and "Upgrade Error: …" carry no Success/Error font of their own.
        if (succeeded is null && (text.Contains("failed to install", StringComparison.OrdinalIgnoreCase) ||
                                  text.StartsWith("Upgrade Error", StringComparison.OrdinalIgnoreCase)))
            succeeded = false;
        var step = new DnnInstallStep(text, succeeded);
        if (succeeded == false) _failedSteps.Add(step);
        return step;
    }

    [GeneratedRegex(@"<br\s*/?>|</h[1-6]>|\n", RegexOptions.IgnoreCase)]
    private static partial Regex Delimiter();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // DNN puts its own elapsed time first: "00:00:12.345 - Installing …".
    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(\.\d{1,7})?\s*-\s*")]
    private static partial Regex Elapsed();

    [GeneratedRegex(@"<font color='(green|red)'>\s*(Success|Error!)\s*</font>", RegexOptions.IgnoreCase)]
    private static partial Regex StatusFont();
}
