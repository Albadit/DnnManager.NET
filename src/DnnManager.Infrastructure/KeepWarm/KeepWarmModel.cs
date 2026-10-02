namespace DnnManager.Infrastructure.KeepWarm;

/// <summary>Where a site's keep warm stands - what its flame shows.</summary>
public enum KeepWarmState
{
    /// <summary>The site isn't kept warm.</summary>
    Off,

    /// <summary>Switched on, the first request is coming - DNN Manager has just started, or another site is warming up.</summary>
    Waiting,

    /// <summary>A warm-up request is on its way: DNN is starting up.</summary>
    WarmingUp,

    /// <summary>The site answered the last request (or is in use anyway).</summary>
    Warm,

    /// <summary>Nothing is sent right now - the site or IIS is stopped, an operation runs, a debugger is attached…</summary>
    Paused,

    /// <summary>The last request failed; it is tried again in a while.</summary>
    Failing,

    /// <summary>Failed several times in a row - not tried again until the site is started, Check now is pressed or the PC wakes up.</summary>
    Stopped
}

/// <summary>A site's keep warm for people: its state and one line about it.</summary>
/// <param name="Text">e.g. "Warm - answered in 12 ms at 14:02".</param>
public sealed record KeepWarmStatus(KeepWarmState State, string Text)
{
    public static readonly KeepWarmStatus Off = new(KeepWarmState.Off, "Off");

    /// <summary>The site is switched to "keep warm", whatever it is doing right now.</summary>
    public bool IsOn => State != KeepWarmState.Off;
}

/// <summary>Something about a site's keep warm worth a line in the activity log.</summary>
public sealed record KeepWarmNotice(string Site, string Message, bool IsWarning);

/// <summary>What a keep-warm request is for.</summary>
public enum KeepWarmRequestKind
{
    /// <summary>The light keep-alive page, to a site that has a worker process.</summary>
    Ping,

    /// <summary>The warm-up page, to a site without a worker process (or one that answered slowly): DNN starts up.</summary>
    WarmUp
}

public enum KeepWarmOutcomeKind
{
    /// <summary>The site answered - its worker process and DNN are up.</summary>
    Ok,

    /// <summary>The keep-alive page doesn't exist (404) - the warm-up page is used instead.</summary>
    PingPathMissing,

    /// <summary>The site answered with an error (HTTP 500, DNN's error page).</summary>
    Failed,

    /// <summary>No answer: refused, timed out, or HTTP 502/503/504.</summary>
    NotAnswering,

    /// <summary>Not sent: the site served other requests since the last look - it is in use, so warm anyway.</summary>
    InUse,

    /// <summary>Not sent: the SQL Server container the site's database is on doesn't answer - warming up would fail.</summary>
    SqlDown,
}

/// <summary>What a request (and the redirects it followed) found.</summary>
/// <param name="Elapsed">Until the first response's headers - how long the site took to answer.</param>
/// <param name="Reason">For a failure: what went wrong, for people.</param>
/// <param name="Note">Something about an answer that is still fine, e.g. DNN's installer waiting.</param>
/// <param name="Requests">How many requests were sent (redirects followed count).</param>
/// <param name="RequestsServed">For <see cref="KeepWarmOutcomeKind.InUse"/> and pings: the site's request counter when it was looked at.</param>
/// <param name="Throttled">DNN Manager was in efficiency mode meanwhile: <see cref="Elapsed"/> can be longer than the site took.</param>
/// <param name="TimedOut">No answer came within the request's time limit.</param>
public sealed record KeepWarmOutcome(
    KeepWarmOutcomeKind Kind,
    TimeSpan Elapsed = default,
    int? StatusCode = null,
    string? Reason = null,
    string? Note = null,
    int Requests = 0,
    long? RequestsServed = null,
    bool Throttled = false,
    bool TimedOut = false)
{
    /// <summary>The answer took long enough to say DNN had to start for it.</summary>
    public bool Slow => Elapsed > (Throttled ? KeepWarmRules.SlowAfterWhileThrottled : KeepWarmRules.SlowAfter);
}

/// <summary>What keep warm holds back while one of DNN Manager's operations runs.</summary>
/// <param name="Everything">Nothing is sent at all - IIS itself is being started, stopped or restarted.</param>
/// <param name="Sites">The sites the operation is about: nothing is sent to them; the others are kept warm as usual.</param>
public sealed record KeepWarmPause(bool Everything, IReadOnlyCollection<string>? Sites)
{
    public static KeepWarmPause All { get; } = new(true, null);

    public static KeepWarmPause For(IReadOnlyCollection<string> sites) => new(false, sites);

    /// <summary>
    /// Another operation (a new project, an import, a backup…): running sites are kept warm, but no site is warmed up
    /// meanwhile - a cold start costs seconds of CPU the operation is using.
    /// </summary>
    public static KeepWarmPause WarmUps { get; } = new(false, null);
}
