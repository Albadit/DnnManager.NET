using DnnManager.Application.Configuration;

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

/// <summary>A version of the upgrade path as Settings shows it: what the step to it needs (0: nothing) and its note in the plan.</summary>
public sealed record DnnPathVersion(Version Version, int NetFrameworkRelease, int SqlServerMajor, string Note);

/// <summary>
/// DNN's suggested upgrade path (docs.dnncommunity.org → Getting started → Setup → Upgrades → Suggested upgrade path):
/// a site is upgraded through each listed version in turn, never straight to the newest. A version between two listed
/// ones is upgraded to the next listed one first. <see cref="DnnUpgradeKnowledge.Steps"/> is the knowledge base - maintained here, in one
/// place (<see cref="DnnUpgradeKnowledge"/> holds what is known about each step); <see cref="Chain"/> works out the steps
/// from a site's version to the one asked for. The path can be edited in Settings (<see cref="DnnUpgradeStopSetting"/>):
/// versions added, changed and taken off. The knowledge base stays as it is - the edits are applied to the chain, and
/// a step they make says so in its warnings.
/// </summary>
public static class DnnUpgradePath
{
    public const string Source = "https://docs.dnncommunity.org/content/getting-started/setup/upgrades/suggested-upgrade-path/index.html";

    /// <summary>The listed versions, oldest first.</summary>
    public static IReadOnlyList<Version> Versions { get; } = DnnUpgradeKnowledge.Steps.Select(s => s.From).Append(DnnUpgradeKnowledge.Steps[^1].To).ToList();

    /// <summary>The oldest version GitHub publishes packages of - every step to an older version is <see cref="DnnUpgradeMethod.Manual"/>.</summary>
    public static Version OldestOnGitHub { get; } = new(7, 4, 2);

    /// <summary>What Settings offers as a version's .NET Framework minimum, as the v4 Full key's <c>Release</c> number (4.5 to 4.8.1).</summary>
    public static IReadOnlyList<int> NetFrameworkChoices { get; } = [378389, 378675, 379893, 393295, 394254, 394802, 460798, 461308, 461808, 528040, 533320];

    /// <summary>
    /// What Settings offers as a version's SQL Server minimum, by major version (2008 on) - the last ones are SQL Servers
    /// that don't exist yet: a later DNN may need one when there is no newer DNN Manager to offer it.
    /// </summary>
    public static IReadOnlyList<int> SqlServerChoices { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

    /// <summary>An edit made in Settings, by the version it is about.</summary>
    private sealed record Edit(Version Version, DnnUpgradeStopSetting Setting)
    {
        /// <summary>A change of one of the path's own versions: what it says replaces what is known.</summary>
        public bool Replaces => Setting.ReplacesListed && !Setting.Removed && Versions.Contains(Version);
        /// <summary>A version added: it only raises what is known, from its version on.</summary>
        public bool Adds => !Setting.ReplacesListed && !Setting.Removed;
    }

    private static List<Edit> EditsOf(IReadOnlyList<DnnUpgradeStopSetting>? edits) =>
        (edits ?? []).Select(s => SettingRules.UpgradeStopVersion(s.Version) is { } version ? new Edit(version, s) : null).OfType<Edit>().ToList();

    /// <summary>The path's versions with the edits: the ones taken off left out, the ones added in, oldest first.</summary>
    private static List<Version> PathOf(IReadOnlyList<Edit> edits)
    {
        var removed = edits.Where(e => e.Setting.Removed).Select(e => e.Version).ToHashSet();
        var path = Versions.Concat(edits.Where(e => !e.Setting.Removed).Select(e => e.Version)).Where(v => !removed.Contains(v)).Distinct().Order().ToList();
        // The rules keep one version at least (SettingRules.UpgradeStopsProblem); a path of none would be DNN's own.
        return path.Count > 0 ? path : [.. Versions];
    }

    /// <summary>
    /// The steps from <paramref name="current"/> to <paramref name="target"/>, in order: through every version of the path
    /// above the current one and below the target, then to the target itself. A step that isn't one of the path's own
    /// (to a version between two listed ones, or past the newest listed) carries what is known about the path's steps it
    /// spans, and says it isn't listed. <paramref name="edits"/> are the edits made in Settings: versions added (stops like
    /// the listed ones), changed or taken off. Empty when the target isn't newer.
    /// </summary>
    public static IReadOnlyList<DnnUpgradeStep> Chain(Version current, Version target, IReadOnlyList<DnnUpgradeStopSetting>? edits = null)
    {
        current = Normalize(current);
        target = Normalize(target);
        var steps = new List<DnnUpgradeStep>();
        if (target <= current) return steps;
        var parsed = EditsOf(edits);
        var path = PathOf(parsed);
        var stops = path.Where(v => v > current && v < target).Append(target).ToList();
        var from = current;
        foreach (var to in stops)
        {
            steps.Add(WithEdits(StepFor(from, to, path), parsed, path));
            from = to;
        }
        return steps;
    }

    /// <summary>What is known about going from <paramref name="from"/> to <paramref name="to"/> - the path's own step when it is one.</summary>
    private static DnnUpgradeStep StepFor(Version from, Version to, IReadOnlyList<Version> path)
    {
        if (DnnUpgradeKnowledge.Steps.FirstOrDefault(s => s.From == from && s.To == to) is { } listed) return listed;
        // The path's steps this one spans - one, unless a listed version was taken off - or, past the newest listed
        // version, the newest step.
        var spanned = DnnUpgradeKnowledge.Steps.Where(s => s.To > from && s.From < to).ToList();
        var known = spanned.Count > 0 ? Merge(spanned) : to > Versions[^1] ? DnnUpgradeKnowledge.Steps[^1] : null;
        var method = to < OldestOnGitHub || from < Versions[0] || spanned.Any(s => s.Method == DnnUpgradeMethod.Manual)
            ? DnnUpgradeMethod.Manual : MethodFor(from);
        // Said of the version that is off the path - the site's own (a hotfix release such as 9.13.10) or the target.
        // None when both are on it: a step to or from a version added in Settings, which says so itself.
        var note = to > path[^1]
            ? $"DNN {DnnUpgradeStep.Name(to)} is newer than the path's newest listed version ({DnnUpgradeStep.Name(path[^1])}) - check DNN's upgrade path " +
              "for anything it needs, and add what it lists in Settings → DNN releases."
            : from < Versions[0]
                ? $"DNN {DnnUpgradeStep.Name(from)} is older than the path's oldest listed version ({DnnUpgradeStep.Name(Versions[0])}) - DNN Manager doesn't know how to upgrade it."
                : !path.Contains(from)
                    ? $"Not one of the path's own steps: the site's DNN {DnnUpgradeStep.Name(from)} lies between its listed versions."
                    : !path.Contains(to)
                        ? $"Not one of the path's own steps: DNN {DnnUpgradeStep.Name(to)} lies between its listed versions."
                        : null;
        var skipped = Versions.Where(v => v > from && v < to && !path.Contains(v)).Select(DnnUpgradeStep.Name).ToList();
        var skips = skipped.Count == 0 ? null
            : $"DNN's path goes through DNN {string.Join(", ", skipped)} on the way - taken off the path in Settings → DNN releases, so this step skips " +
              $"{(skipped.Count == 1 ? "it" : "them")}. DNN Manager has no tests of this step.";
        return (known ?? new DnnUpgradeStep { From = from, To = to }) with
        {
            From = from, To = to, Method = method,
            Warnings = [.. new[] { skips, note }.OfType<string>(), .. known?.Warnings ?? []], Tested = []
        };
    }

    /// <summary>The path's steps <paramref name="steps"/> as one: the highest minimum of each kind and everything said about any of them.</summary>
    private static DnnUpgradeStep Merge(IReadOnlyList<DnnUpgradeStep> steps) => steps.Count == 1 ? steps[0] : steps[^1] with
    {
        Requirements = steps.SelectMany(s => s.Requirements).GroupBy(r => r.Kind).Select(g => g.MaxBy(r => r.Minimum)!).ToList(),
        Warnings = steps.SelectMany(s => s.Warnings).Distinct().ToList(),
        BreakingChanges = steps.SelectMany(s => s.BreakingChanges).Distinct().ToList(),
        KnownIssues = steps.SelectMany(s => s.KnownIssues).Distinct().ToList(),
        RemovesTelerik = steps.Any(s => s.RemovesTelerik)
    };

    /// <summary>
    /// A step with what the edits made in Settings say about it. A changed version of the path - the step's target, or the
    /// version of the path it falls in - replaces the minimums known for it, lower ones too. An added version's minimum
    /// holds from its version on, so for this step and every later one, and only ever raises what is known. A version's
    /// note goes with the step to it.
    /// </summary>
    private static DnnUpgradeStep WithEdits(DnnUpgradeStep step, IReadOnlyList<Edit> edits, IReadOnlyList<Version> path)
    {
        if (edits.Count == 0) return step;
        var requirements = step.Requirements.ToList();
        var row = path.FirstOrDefault(v => v >= step.To) ?? path[^1];
        if (edits.FirstOrDefault(e => e.Replaces && e.Version == row) is { } changed)
        {
            Replace(DnnRequirementKind.NetFramework, changed.Setting.NetFrameworkRelease, DnnSiteFacts.NetFrameworkName);
            Replace(DnnRequirementKind.SqlServer, changed.Setting.SqlServerMajor, DnnSiteFacts.SqlServerName);
        }
        var added = edits.Where(e => e.Adds && e.Version <= step.To).ToList();
        if (added.Count > 0)
        {
            Raise(DnnRequirementKind.NetFramework, added.Max(s => s.Setting.NetFrameworkRelease), DnnSiteFacts.NetFrameworkName);
            Raise(DnnRequirementKind.SqlServer, added.Max(s => s.Setting.SqlServerMajor), DnnSiteFacts.SqlServerName);
        }

        var warnings = new List<string>();
        if (edits.FirstOrDefault(e => !e.Setting.Removed && e.Version == step.To) is { } own)
        {
            if (!Versions.Contains(own.Version))
                warnings.Add($"You added DNN {DnnUpgradeStep.Name(own.Version)} to the path (Settings → DNN releases): DNN Manager has no tests of this step, so read DNN's release notes for it.");
            else if (own.Replaces)
                warnings.Add($"You changed what DNN {DnnUpgradeStep.Name(own.Version)} needs in Settings → DNN releases: its checks are yours, not DNN's.");
            if (own.Setting.Note.Trim() is { Length: > 0 } note) warnings.Add(note);
        }
        return step with { Requirements = requirements, Warnings = [.. warnings, .. step.Warnings] };

        void Replace(DnnRequirementKind kind, int minimum, Func<int, string> name)
        {
            // Left as DNN Manager knows it: its own wording and source stay.
            if (minimum == ListedMinimum(row, kind)) return;
            requirements.RemoveAll(r => r.Kind == kind);
            if (minimum > 0) requirements.Add(new DnnRequirement(kind, minimum, name(minimum), "Changed in Settings → DNN releases"));
        }

        void Raise(DnnRequirementKind kind, int minimum, Func<int, string> name)
        {
            if (minimum <= 0 || requirements.Any(r => r.Kind == kind && r.Minimum >= minimum)) return;
            requirements.RemoveAll(r => r.Kind == kind);
            requirements.Add(new DnnRequirement(kind, minimum, name(minimum), "Added in Settings → DNN releases"));
        }
    }

    /// <summary>What DNN Manager knows the step to <paramref name="version"/>, one of the path's own, needs of <paramref name="kind"/>; 0 for nothing.</summary>
    public static int ListedMinimum(Version version, DnnRequirementKind kind) =>
        DnnUpgradeKnowledge.Steps.FirstOrDefault(s => s.To == version)?.Requirements.FirstOrDefault(r => r.Kind == kind)?.Minimum ?? 0;

    /// <summary>One of the path's own versions as DNN Manager knows it.</summary>
    public static DnnPathVersion Listed(Version version) =>
        new(version, ListedMinimum(version, DnnRequirementKind.NetFramework), ListedMinimum(version, DnnRequirementKind.SqlServer), "");

    /// <summary>Whether the step to <paramref name="version"/>, one of the path's own, is done by hand (or there is none: the oldest).</summary>
    public static bool IsManual(Version version) =>
        version == Versions[0] || DnnUpgradeKnowledge.Steps.FirstOrDefault(s => s.To == version)?.Method == DnnUpgradeMethod.Manual;

    /// <summary>
    /// The whole path with <paramref name="edits"/>, oldest first, as Settings shows it: each version with what the step
    /// to it needs and its note.
    /// </summary>
    public static IReadOnlyList<DnnPathVersion> PathWith(IReadOnlyList<DnnUpgradeStopSetting>? edits)
    {
        var parsed = EditsOf(edits);
        return PathOf(parsed).Select(v =>
        {
            if (parsed.FirstOrDefault(e => e.Version == v && !e.Setting.Removed) is not { } edit) return Listed(v);
            var own = new DnnPathVersion(v, edit.Setting.NetFrameworkRelease, edit.Setting.SqlServerMajor, edit.Setting.Note.Trim());
            if (!Versions.Contains(v) || edit.Replaces) return own;
            // Added before DNN Manager listed it: it only raises what is known.
            var listed = Listed(v);
            return own with
            {
                NetFrameworkRelease = Math.Max(own.NetFrameworkRelease, listed.NetFrameworkRelease),
                SqlServerMajor = Math.Max(own.SqlServerMajor, listed.SqlServerMajor)
            };
        }).ToList();
    }

    /// <summary>
    /// The edits that make <paramref name="path"/> of the path DNN Manager knows: its versions left out taken off, the
    /// ones changed and the ones added. One as it was in <paramref name="before"/> keeps its edit as it was - an added
    /// version DNN Manager has learned since still only raises what it knows.
    /// </summary>
    public static List<DnnUpgradeStopSetting> EditsFor(IEnumerable<DnnPathVersion> path, IReadOnlyList<DnnUpgradeStopSetting>? before = null)
    {
        var rows = path.Select(r => r with { Version = Normalize(r.Version), Note = r.Note.Trim() }).ToList();
        var was = PathWith(before).ToDictionary(r => r.Version);
        var previous = EditsOf(before).Where(e => !e.Setting.Removed).DistinctBy(e => e.Version).ToDictionary(e => e.Version);
        var edits = new List<DnnUpgradeStopSetting>();
        foreach (var row in rows.OrderBy(r => r.Version))
        {
            var listed = Versions.Contains(row.Version);
            if (was.TryGetValue(row.Version, out var old) && old == row && previous.TryGetValue(row.Version, out var kept)) edits.Add(kept.Setting);
            else if (!listed || row != Listed(row.Version))
                edits.Add(new DnnUpgradeStopSetting
                {
                    Version = row.Version.ToString(), NetFrameworkRelease = row.NetFrameworkRelease, SqlServerMajor = row.SqlServerMajor,
                    Note = row.Note, ReplacesListed = listed
                });
        }
        var onPath = rows.Select(r => r.Version).ToHashSet();
        edits.AddRange(Versions.Where(v => !onPath.Contains(v)).Select(v => new DnnUpgradeStopSetting { Version = v.ToString(), Removed = true }));
        return edits.OrderBy(e => SettingRules.UpgradeStopVersion(e.Version)).ToList();
    }

    /// <summary>DNN 10.2 brought its own local upgrade: from a 10.2+ site on, that is how its files go in.</summary>
    public static DnnUpgradeMethod MethodFor(Version from) => from >= new Version(10, 2, 0) ? DnnUpgradeMethod.LocalUpgrade : DnnUpgradeMethod.UpgradePackage;

    /// <summary>10.3.3 of 10.3.3.0 (or 9.3.2 of 9.3.2.24): DNN's versions compare by major, minor and build.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
