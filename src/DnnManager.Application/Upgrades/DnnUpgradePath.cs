namespace DnnManager.Application.Upgrades;

/// <summary>How a step of the upgrade path is carried out.</summary>
public enum DnnUpgradeMethod
{
    /// <summary>
    /// The release's <c>…_Upgrade.zip</c> over the site's files (never its web.config), then DNN's own unattended upgrade -
    /// <c>Install/Install.aspx?mode=upgrade</c> - which runs its database scripts and upgrades its extensions.
    /// </summary>
    UpgradePackage,

    /// <summary>
    /// For a site already on DNN 10.2 or newer: the release's <c>…_Install.zip</c> put in as DNN's own local upgrade does
    /// (Settings → Servers → System Info → Upgrades): each assembly with its binding redirect in web.config, then the rest
    /// but web.config - then DNN's unattended upgrade as above. Unzipping the upgrade package over a 10.2+ site leaves its
    /// binding redirects behind the new assemblies, and the site no longer starts (tested: 10.2.5 → 10.3.3, AngleSharp).
    /// </summary>
    LocalUpgrade,

    /// <summary>
    /// Not carried out by DNN Manager: GitHub has no packages of the version (DNN 6 and older), or it is older than the
    /// path knows. The step has to be done by hand, as DNN's documentation describes it.
    /// </summary>
    Manual
}

/// <summary>What a requirement is about - how the analyser checks it.</summary>
public enum DnnRequirementKind
{
    /// <summary>The .NET Framework installed on this PC, as the <c>Release</c> number of the v4 Full registry key.</summary>
    NetFramework,

    /// <summary>The SQL Server the site's database is on, by its major version (14 = SQL Server 2017).</summary>
    SqlServer
}

/// <summary>A minimum the target version of a step needs - checked before the upgrade starts; not met, it is blocking.</summary>
/// <param name="Minimum">.NET Framework: the <c>Release</c> number (461808 = 4.7.2, 528040 = 4.8); SQL Server: its major version.</param>
/// <param name="Text">How people say it: ".NET Framework 4.8", "SQL Server 2017".</param>
public sealed record DnnRequirement(DnnRequirementKind Kind, int Minimum, string Text, string Source);

/// <summary>
/// One step of DNN's suggested upgrade path - from one listed version to the next - and what is known about it: what the
/// target needs, what changed, what went wrong for others, and how it was tested here. The knowledge base the upgrade
/// plan is made from (<see cref="DnnUpgradePath"/>).
/// </summary>
public sealed record DnnUpgradeStep
{
    public required Version From { get; init; }
    public required Version To { get; init; }
    public DnnUpgradeMethod Method { get; init; } = DnnUpgradeMethod.UpgradePackage;
    public IReadOnlyList<DnnRequirement> Requirements { get; init; } = [];
    /// <summary>What to look out for - shown in the plan as warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>What the target version removed or changed that may break a site or its extensions.</summary>
    public IReadOnlyList<string> BreakingChanges { get; init; } = [];
    /// <summary>Problems seen with this step - DNN's issues, the community's reports, these tests - and their fixes.</summary>
    public IReadOnlyList<string> KnownIssues { get; init; } = [];
    /// <summary>Whether the site may still depend on Telerik once it runs the target version (DNN 10 removed it).</summary>
    public bool RemovesTelerik { get; init; }
    /// <summary>How DNN Manager's tests carried this step out on real sites - empty when it wasn't tested.</summary>
    public IReadOnlyList<string> Tested { get; init; } = [];

    /// <summary>DNN's way of writing a version in the path: 09.13.09.</summary>
    public static string Name(Version version) => $"{version.Major:00}.{version.Minor:00}.{Math.Max(version.Build, 0):00}";

    public override string ToString() => $"{Name(From)} → {Name(To)}";
}

/// <summary>
/// DNN's suggested upgrade path (docs.dnncommunity.org → Getting started → Setup → Upgrades → Suggested upgrade path):
/// a site is upgraded through each listed version in turn, never straight to the newest. A version between two listed
/// ones is upgraded to the next listed one first. <see cref="Steps"/> is the knowledge base - maintained here, in one
/// place (<see cref="DnnUpgradeKnowledge"/> holds what is known about each step); <see cref="Chain"/> works out the steps
/// from a site's version to the one asked for.
/// </summary>
public static class DnnUpgradePath
{
    public const string Source = "https://docs.dnncommunity.org/content/getting-started/setup/upgrades/suggested-upgrade-path/index.html";

    /// <summary>The listed versions, oldest first.</summary>
    public static IReadOnlyList<Version> Versions { get; } = DnnUpgradeKnowledge.Steps.Select(s => s.From).Append(DnnUpgradeKnowledge.Steps[^1].To).ToList();

    /// <summary>The oldest version GitHub publishes packages of - every step to an older version is <see cref="DnnUpgradeMethod.Manual"/>.</summary>
    public static Version OldestOnGitHub { get; } = new(7, 4, 2);

    /// <summary>
    /// The steps from <paramref name="current"/> to <paramref name="target"/>, in order: through every listed version
    /// above the current one and below the target, then to the target itself. A step that isn't one of the path's own
    /// (to a version between two listed ones, or past the newest listed) carries what is known about the path's step it
    /// falls in, and says it isn't listed. Empty when the target isn't newer.
    /// </summary>
    public static IReadOnlyList<DnnUpgradeStep> Chain(Version current, Version target)
    {
        current = Normalize(current);
        target = Normalize(target);
        var steps = new List<DnnUpgradeStep>();
        if (target <= current) return steps;
        var stops = Versions.Where(v => v > current && v < target).Append(target).ToList();
        var from = current;
        foreach (var to in stops)
        {
            steps.Add(StepFor(from, to));
            from = to;
        }
        return steps;
    }

    /// <summary>What is known about going from <paramref name="from"/> to <paramref name="to"/> - the path's own step when it is one.</summary>
    private static DnnUpgradeStep StepFor(Version from, Version to)
    {
        if (DnnUpgradeKnowledge.Steps.FirstOrDefault(s => s.From == from && s.To == to) is { } listed) return listed;
        // The path's step whose target this one reaches - or, past the newest listed version, the newest step.
        var known = DnnUpgradeKnowledge.Steps.FirstOrDefault(s => to <= s.To && to > s.From)
                    ?? (to > Versions[^1] ? DnnUpgradeKnowledge.Steps[^1] : null);
        var method = to < OldestOnGitHub || from < Versions[0] ? DnnUpgradeMethod.Manual : MethodFor(from);
        var note = to > Versions[^1]
            ? $"DNN {DnnUpgradeStep.Name(to)} is newer than the path's newest listed version ({DnnUpgradeStep.Name(Versions[^1])}) - check DNN's upgrade path for anything it needs."
            : from < Versions[0]
                ? $"DNN {DnnUpgradeStep.Name(from)} is older than the path's oldest listed version ({DnnUpgradeStep.Name(Versions[0])}) - DNN Manager doesn't know how to upgrade it."
                : $"Not one of the path's own steps: DNN {DnnUpgradeStep.Name(to)} lies between its listed versions.";
        return (known ?? new DnnUpgradeStep { From = from, To = to }) with
        {
            From = from, To = to, Method = method, Warnings = [note, .. known?.Warnings ?? []], Tested = []
        };
    }

    /// <summary>DNN 10.2 brought its own local upgrade: from a 10.2+ site on, that is how its files go in.</summary>
    public static DnnUpgradeMethod MethodFor(Version from) => from >= new Version(10, 2, 0) ? DnnUpgradeMethod.LocalUpgrade : DnnUpgradeMethod.UpgradePackage;

    /// <summary>10.3.3 of 10.3.3.0 (or 9.3.2 of 9.3.2.24): DNN's versions compare by major, minor and build.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
