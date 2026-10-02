using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.KeepWarm;

/// <summary>
/// The rules keep warm goes by, apart from the service that follows them - when to send, what an answer means, how
/// long to wait after a failure.
/// </summary>
/// <remarks>
/// Why a local DNN site is slow after a while without visits: IIS shuts an app pool's worker process down after its
/// idle time-out (20 minutes by default), and the next request starts a new one - ASP.NET and DNN start up again, which
/// takes 3 to 10 seconds instead of some 30 ms. Recycles (every 29 hours by default, and DNN Manager's own) and IIS
/// starting do the same. So keep warm requests a site before its time-out runs out, and warms it up again as soon as
/// its worker process is gone - with the site's own keep-alive page, which does no database work.
/// </remarks>
public static class KeepWarmRules
{
    /// <summary>
    /// An answer that takes longer than this came from a site that was cold: DNN had to start (3 seconds and more,
    /// against some 10 ms warm).
    /// </summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The same while DNN Manager is in efficiency mode: its threads may wait seconds for a processor while others are
    /// busy (Windows gives a waiting thread its turn after about 4 s), so a quick answer can be measured as a slow one.
    /// </summary>
    public static readonly TimeSpan SlowAfterWhileThrottled = TimeSpan.FromSeconds(5);

    /// <summary>Never more often than this, whatever the app pool's idle time-out.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The interval is at most this part of the idle time-out: a request skipped because the site was in use may leave
    /// almost two intervals between the last request and the next - still well within the time-out.
    /// </summary>
    public const double IdleTimeoutShare = 0.4;

    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A cold DNN can take a while to start, longer after a rebuild.</summary>
    public static readonly TimeSpan WarmUpTimeout = TimeSpan.FromMinutes(2);

    /// <summary>After DNN Manager starts, sites without a worker process are warmed up only after this - Windows may still be starting.</summary>
    public static readonly TimeSpan StartUpDelay = TimeSpan.FromMinutes(1);

    /// <summary>A worker process that ends this soon after a warm-up started it, failed (a site that keeps crashing).</summary>
    public static readonly TimeSpan DiedSoonAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Failures in a row after which a site isn't tried again until something changes - after the retries 1, 2 and 5
    /// minutes after the first three.
    /// </summary>
    public const int FailuresBeforeStop = 4;

    /// <summary>At most this many requests at once, for all sites together.</summary>
    public const int MaxInFlight = 2;

    /// <summary>Redirects to the same site a warm-up follows.</summary>
    public const int MaxRedirects = 3;

    private static readonly TimeSpan[] Backoffs = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// The time between two requests: <paramref name="pingMinutes"/>, or less when the app pool's
    /// <paramref name="idleTimeout"/> needs it. Also when IIS never shuts the worker process down for being idle: DNN
    /// itself can still stop (a rebuild restarts it inside the same worker process), and the next request starts it.
    /// </summary>
    public static TimeSpan Interval(int pingMinutes, TimeSpan? idleTimeout)
    {
        var setting = TimeSpan.FromMinutes(Math.Max(1, pingMinutes));
        if (idleTimeout is not { } idle || idle <= TimeSpan.Zero) return setting;
        var safe = idle * IdleTimeoutShare;
        var interval = safe < setting ? safe : setting;
        return interval < MinInterval ? MinInterval : interval;
    }

    /// <summary>
    /// Whether a request may be skipped because the site served others since the last look. The next one then comes
    /// two intervals after the last look at most - which has to stay within the idle time-out. Not so when the shortest
    /// interval is already close to it (an idle time-out of a minute).
    /// </summary>
    public static bool MaySkipWhenInUse(string site, TimeSpan interval, TimeSpan? idleTimeout) =>
        idleTimeout is not { } idle || idle <= TimeSpan.Zero || 2 * (interval + Jitter(site, interval)) < idle;

    /// <summary>How long to wait after the <paramref name="failures"/>-th failure in a row: 1, 2, then 5 minutes.</summary>
    public static TimeSpan Backoff(int failures) => Backoffs[Math.Clamp(failures - 1, 0, Backoffs.Length - 1)];

    /// <summary>
    /// A small offset of its own for each site (up to a tenth of the interval, at most 15 s), so sites switched on
    /// together don't send at the same moment every time. The same for a site every time.
    /// </summary>
    public static TimeSpan Jitter(string site, TimeSpan interval)
    {
        var range = Math.Min(interval.TotalMilliseconds / 10, 15_000);
        if (range < 1) return TimeSpan.Zero;
        uint hash = 2166136261;
        foreach (var c in site.ToUpperInvariant()) hash = (hash ^ c) * 16777619;
        return TimeSpan.FromMilliseconds(hash % (uint)range);
    }

    /// <summary>What a response means, and - for a warm-up - which redirect to follow.</summary>
    /// <param name="FollowPath">The same site's page to request next; null to stop here.</param>
    public sealed record Verdict(KeepWarmOutcomeKind Kind, string? Reason = null, string? Note = null, string? FollowPath = null);

    /// <summary>
    /// What a response to a <paramref name="kind"/> request for <paramref name="requested"/> means. Any answer from the
    /// site keeps it warm - its worker process and DNN run - except an error. DNN's installer is never followed into:
    /// requesting it can install or upgrade DNN.
    /// </summary>
    /// <param name="requested">The address the request was sent to as people know it (scheme, host header, path).</param>
    /// <param name="location">The response's Location header, if any.</param>
    /// <param name="bodyStart">The start of the response's body.</param>
    public static Verdict Classify(KeepWarmRequestKind kind, Uri requested, int status, Uri? location, string bodyStart)
    {
        switch (status)
        {
            case >= 200 and < 300:
                return bodyStart.Contains("InstallWizard", StringComparison.OrdinalIgnoreCase)
                    ? new Verdict(KeepWarmOutcomeKind.Ok, Note: InstallerWaiting)
                    : new Verdict(KeepWarmOutcomeKind.Ok);

            case >= 300 and < 400:
                if (location is null) return new Verdict(KeepWarmOutcomeKind.Ok);
                var target = location.IsAbsoluteUri ? location : new Uri(requested, location);
                if (target.AbsolutePath.Contains("ErrorPage", StringComparison.OrdinalIgnoreCase) ||
                    target.Query.Contains("error=", StringComparison.OrdinalIgnoreCase))
                    return new Verdict(KeepWarmOutcomeKind.Failed, ErrorPageReason(target));
                // Another site (or https): the site answered, nothing to follow.
                var sameSite = Uri.Compare(target, requested, UriComponents.SchemeAndServer, UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;
                if (!sameSite) return new Verdict(KeepWarmOutcomeKind.Ok);
                if (KeepWarmSettings.IsInstallerPath(target.PathAndQuery)) return new Verdict(KeepWarmOutcomeKind.Ok, Note: InstallerWaiting);
                // Only a warm-up follows: the page it lands on is compiled too.
                return kind == KeepWarmRequestKind.WarmUp
                    ? new Verdict(KeepWarmOutcomeKind.Ok, FollowPath: target.PathAndQuery)
                    : new Verdict(KeepWarmOutcomeKind.Ok);

            case 404 when kind == KeepWarmRequestKind.Ping:
                return new Verdict(KeepWarmOutcomeKind.PingPathMissing);

            // Not found, forbidden, sign in first… - the site answered, so it runs.
            case >= 400 and < 500:
                return new Verdict(KeepWarmOutcomeKind.Ok);

            case 502 or 503 or 504:
                return new Verdict(KeepWarmOutcomeKind.NotAnswering, status == 503
                    ? "the site answers 503 Service Unavailable - its app pool may be stopping or failing"
                    : $"the site answers HTTP {status}");

            default:
                return new Verdict(KeepWarmOutcomeKind.Failed, $@"the site answers HTTP {status} - see its logs in Portals\_default\Logs");
        }
    }

    private const string InstallerWaiting = "DNN's installer is waiting - open the site to finish it";

    // DNN's error page says what went wrong in its query string (?status=500&error=…) - e.g. that the database didn't answer.
    private static string ErrorPageReason(Uri target)
    {
        var error = target.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0].Equals("error", StringComparison.OrdinalIgnoreCase))?[1];
        var text = error is null ? "" : Uri.UnescapeDataString(error.Replace('+', ' ')).Trim();
        if (text.Length > 120) text = text[..120] + "…";
        return text.Length > 0 ? $"DNN shows its error page: {text}" : @"DNN shows its error page - see its logs in Portals\_default\Logs";
    }

    /// <summary>"12 ms", "3.4 s" - how long an answer took.</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromSeconds(1) ? $"{Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds))} ms" : $"{elapsed.TotalSeconds:0.0} s";

    /// <summary>"5 minutes", "1 minute", "45 seconds", "1.5 minutes".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return $"{(int)Math.Round(span.TotalSeconds)} seconds";
        var minutes = span.TotalMinutes;
        if (Math.Abs(minutes - Math.Round(minutes)) < 0.01)
            return (int)Math.Round(minutes) == 1 ? "1 minute" : $"{(int)Math.Round(minutes)} minutes";
        return $"{minutes:0.#} minutes";
    }
}
