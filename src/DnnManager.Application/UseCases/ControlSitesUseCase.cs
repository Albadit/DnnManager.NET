using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

public enum SiteAction { Start, Stop, Restart }

/// <summary>Starts, stops or restarts the IIS sites of one or more projects - the Projects table's row and bulk actions.</summary>
public sealed class ControlSitesUseCase
{
    private readonly IIisManager _iis;

    public ControlSitesUseCase(IIisManager iis) => _iis = iis;

    /// <summary>
    /// Acts on every project in <paramref name="projectNames"/>, going on past one that fails; fails when any did -
    /// with the reason itself when it was about one site, else with which ones (each one's reason is reported).
    /// </summary>
    public Task<Result> ExecuteAsync(SiteAction action, IReadOnlyList<string> projectNames, IProgressReporter reporter, CancellationToken ct)
    {
        // A site can't start (or even report its state) while IIS itself is stopped - say what to do instead.
        var server = _iis.GetServerState();
        if (server == IisServerState.NotInstalled)
            return Task.FromResult(Result.Fail("IIS isn't installed - check the Environment page."));
        if (action != SiteAction.Stop && server is IisServerState.Stopped or IisServerState.Stopping)
            return Task.FromResult(Result.Fail("IIS is stopped - start it first (bottom-left of the window)."));

        var single = projectNames.Count == 1;
        var failed = new List<(string Name, string? Error)>();
        foreach (var name in projectNames)
        {
            ct.ThrowIfCancellationRequested();
            var result = action switch
            {
                SiteAction.Start => _iis.StartSite(name),
                SiteAction.Stop => _iis.StopSite(name),
                _ => _iis.RestartSite(name)
            };
            if (result.Success)
            {
                reporter.Success(action switch
                {
                    SiteAction.Start => $"Started '{name}'.",
                    SiteAction.Stop => $"Stopped '{name}'.",
                    _ => $"Restarted '{name}' (its app pool recycled)."
                });
            }
            else
            {
                // One site's reason is the operation's own error - said once, by whoever reports that.
                if (!single) reporter.Fail($"Could not {action.ToString().ToLowerInvariant()} '{name}': {result.Error}");
                failed.Add((name, result.Error));
            }
        }

        if (failed.Count == 0) return Task.FromResult(Result.Ok());
        if (single) return Task.FromResult(Result.Fail(failed[0].Error ?? $"Could not {action.ToString().ToLowerInvariant()} '{failed[0].Name}'."));
        var done = action switch
        {
            SiteAction.Start => "started",
            SiteAction.Stop => "stopped",
            _ => "restarted"
        };
        return Task.FromResult(Result.Fail(
            $"{failed.Count} of {projectNames.Count} could not be {done}: {string.Join(", ", failed.Select(f => f.Name))}."));
    }
}
