namespace DnnManager.Application.Upgrades;

/// <summary>What most likely made an upgrade step fail, and what to do about it.</summary>
public sealed record UpgradeDiagnosis(string Cause, IReadOnlyList<string> Fixes);

/// <summary>
/// Reads what an upgrade step failed with - DNN's answer, the checks, its logs - for the most likely cause and its fixes:
/// what DNN Manager's tests and DNN's issues have shown goes wrong. The first that fits; the general advice otherwise.
/// </summary>
public static class DnnUpgradeDiagnosis
{
    /// <summary>A cause; <paramref name="Weak"/>: one that says little on its own (a time-out, an HTTP 503) - what the logs name comes first.</summary>
    private sealed record Rule(Func<string, bool> Fits, string Cause, string[] Fixes, bool Weak = false);

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static readonly Rule[] Rules =
    [
        new(t => Has(t, "Could not load file or assembly") || Has(t, "manifest definition does not match"),
            "An assembly in bin doesn't match web.config's binding redirect for it - the site can't start with the new files (DNN issues #6782, #6993).",
            ["Compare web.config's <runtime><assemblyBinding> redirect for the assembly named with the version of the DLL in bin.",
             "From DNN 10.2 on, upgrade with DNN's own local upgrade (DNN Manager does so) instead of unzipping the upgrade package."]),
        // Not just a script's log named among those kept: a script that failed, or SQL that DNN logged as failing.
        new(t => Has(t, "Database script") || Has(t, "Error executing SQL") || Has(t, "SqlDataProvider") && Has(t, "stopped at"),
            "One of DNN's database scripts failed.",
            ["The DNN log line above (kept in the step's failed-upgrade folder) names the SQL that failed and SQL Server's error; " +
             @"a script's own log in Providers\DataProviders\SqlDataProvider may add to it.",
             "Third-party objects on DNN's tables (triggers, views, constraints) often make them fail - fix or remove the one named, then upgrade again."]),
        new(t => Has(t, "installBlocker") || Has(t, "currently in progress") || Has(t, "installation/upgrade was in progress"),
            "DNN thinks an installation or upgrade is still running: its installBlocker.lock is there. DNN keeps the lock open " +
            "until its worker process ends, so it can't delete it itself at the end of its upgrade.",
            ["Stop the site, wait for its worker process (w3wp.exe) to end - or end it in Task Manager - then delete installBlocker.lock " +
             "in the site's folder and start the site.",
             "Make sure nobody runs DNN's installer meanwhile."]),
        new(t => Has(t, "Could not connect to database") || Has(t, "couldn't connect to its database") || Has(t, "Login failed") ||
                 Has(t, "A network-related or instance-specific error") || Has(t, "The database can't be read"),
            "The site can't reach its database.",
            ["Check that SQL Server runs and that the site's login (web.config's SiteSqlServer, or the app pool's identity) may sign in and owns the database."]),
        new(t => Has(t, "-5324627661") || Has(t, "CodeDom") || Has(t, "compiler"),
            "The C# compiler package (CodeDom) changed and ASP.NET can't compile the site (DNN issue #6448).",
            ["Uninstall the Microsoft.CodeDom.Providers.DotNetCompilerPlatform 3.6 extension before upgrading, or remove web.config's <system.codedom><compilers>."]),
        new(t => Has(t, "Telerik"),
            "Something still needs Telerik, which DNN 10 removes.",
            ["Update or remove the extensions that use Telerik (DNN 9.8+: Settings → Servers → Telerik removal lists them), then upgrade again."]),
        new(t => Has(t, "Content was lost"),
            "The upgrade changed the site's content - portals, users, roles, pages or permissions are fewer than before.",
            ["Keep the site as the stage's backup has it (DNN Manager puts it back) and look at what the upgrade's database scripts did before trying again."]),
        new(t => Has(t, "can't sign in") || Has(t, "Persona Bar"),
            "The site runs, but signing in or DNN's Persona Bar doesn't work after the upgrade.",
            ["Look at DNN's log (kept with the stage's diagnostics) for errors from the Persona Bar or the membership provider; clearing the site's cache and recycling the app pool often helps."]),
        new(t => Has(t, "HTTP 503") || Has(t, "Service Unavailable"),
            "The site doesn't answer (HTTP 503): its app pool stopped - IIS's rapid-fail protection after crashes - or DNN's lock is still there.",
            ["Read the Windows events kept with the stage's diagnostics (WAS, ASP.NET) for why the worker process stopped."], Weak: true),
        new(t => Has(t, "HTTP 500") || Has(t, "error page") || Has(t, "module error"),
            "The site fails to show its pages after the upgrade.",
            ["The ASP.NET error and DNN's log (kept with the stage's diagnostics) name the exception; an extension built for an older DNN is the usual reason."]),
        new(t => Has(t, "took longer than") || Has(t, "stopped reporting progress") || Has(t, "didn't answer within"),
            "DNN's upgrade took too long or hung.",
            ["A large database or a locked file (antivirus, an editor) slows it down - try again with nothing else using the site's folder."], Weak: true),
        new(t => Has(t, "Expected DNN") || Has(t, "the database at"),
            "DNN's upgrade didn't run or didn't finish: the database isn't at the version of the files.",
            ["DNN's output (kept with the stage's diagnostics) shows where it stopped."]),
    ];

    /// <summary>The most likely cause of <paramref name="error"/>, read together with <paramref name="evidence"/> (logs, check results).</summary>
    public static UpgradeDiagnosis Explain(string error, IEnumerable<string> evidence)
    {
        // What the step failed with first - then what the logs and the checks say; a time-out or an HTTP 503 only when
        // they name nothing more telling (the site timed out because DNN's lock was left, say).
        var all = string.Join("\n", evidence);
        foreach (var (text, weak) in new[] { (error, false), (all, false), (error, true), (all, true) })
            foreach (var rule in Rules.Where(r => r.Weak == weak))
                if (rule.Fits(text)) return new UpgradeDiagnosis(rule.Cause, rule.Fixes);
        return new UpgradeDiagnosis("Unknown - none of the known problems fits.",
            ["Read DNN's output, its logs and the Windows events kept with the stage's diagnostics.", "Ask in DNN's community forums with them."]);
    }
}
