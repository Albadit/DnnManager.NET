using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Prereq;

public sealed class WindowsPrerequisiteChecker : IPrerequisiteChecker
{
    private readonly ProcessRunner _proc;
    private readonly AppOptions _opts;
    private readonly ILogger<WindowsPrerequisiteChecker> _log;

    public WindowsPrerequisiteChecker(ProcessRunner proc, IOptions<AppOptions> opts, ILogger<WindowsPrerequisiteChecker> log)
    {
        _proc = proc; _opts = opts.Value; _log = log;
    }

    public async Task<Result> CheckDockerAsync(IProgressReporter reporter, CancellationToken ct)
    {
        // One call answers both questions: `docker version` prints the client version even when the
        // daemon is down, and exits non-zero unless it could also reach the engine. (It replaces
        // `docker --version` + `docker info`; `info` alone is the slowest docker command we ran.)
        var v = await _proc.RunAsync("docker",
            new[] { "version", "--format", "{{.Client.Version}}|{{.Server.Version}}" }, ct);
        var parts = v.StdOut.Trim().Split('|');
        var client = parts[0].Trim();
        var server = parts.Length > 1 ? parts[1].Trim() : "";

        if (client.Length == 0) { reporter.Fail("Docker CLI not found or failed."); return Result.Fail("Docker missing"); }
        reporter.Success($"Docker version {client}");
        if (!v.Success || server.Length == 0) { reporter.Fail("Docker daemon not running."); return Result.Fail("Docker daemon offline"); }
        reporter.Success($"Docker daemon is running (engine {server}).");
        return Result.Ok();
    }

    public async Task<Result> EnsureIisFeaturesAsync(IProgressReporter reporter, IUserPrompt prompt, CancellationToken ct)
    {
        if (_opts.RequiredIisFeatures.Count == 0)
        {
            reporter.Info("No IIS features configured to check.");
            return Result.Ok();
        }

        // Query every feature from a single PowerShell process. Each powershell.exe launch plus the
        // DISM module load costs most of a second, and there are ~16 features to check.
        var states = await RunPerFeatureAsync(_opts.RequiredIisFeatures,
            "(Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue).State", ct);

        var missing = new List<IisFeatureSetting>();
        foreach (var f in _opts.RequiredIisFeatures)
        {
            if (states.TryGetValue(f.Name, out var state) && state.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                reporter.Success($"{f.Label} ({f.Name})");
            else
                missing.Add(f);
        }
        if (missing.Count == 0) return Result.Ok();

        reporter.Info("Missing IIS features:");
        foreach (var f in missing) reporter.Fail($"{f.Label} ({f.Name})");
        if (!await prompt.ConfirmAsync("Enable them now?", true, ct))
            return Result.Fail("IIS features missing.");

        reporter.Info($"Enabling {missing.Count} feature(s)…");
        var enabled = await RunPerFeatureAsync(missing,
            "try { Enable-WindowsOptionalFeature -Online -FeatureName $n -All -NoRestart -ErrorAction Stop | Out-Null; 'OK' } catch { 'FAIL' }", ct);
        foreach (var f in missing)
        {
            if (enabled.TryGetValue(f.Name, out var r) && r == "OK")
                reporter.Success($"Enabled {f.Label}");
            else
                reporter.Fail($"Failed: {f.Label}");
        }
        reporter.Success("IIS feature changes applied (a reboot may be required).");
        return Result.Ok();
    }

    /// <summary>
    /// Runs <paramref name="perFeature"/> (with the feature name in <c>$n</c>) for every feature inside
    /// one PowerShell process and returns feature name -> the expression's output.
    /// </summary>
    private async Task<Dictionary<string, string>> RunPerFeatureAsync(
        IEnumerable<IisFeatureSetting> features, string perFeature, CancellationToken ct)
    {
        // Names come from appsettings.json - quote them as PowerShell single-quoted literals.
        var names = string.Join(",", features.Select(f => "'" + f.Name.Replace("'", "''") + "'"));
        var script = $"foreach ($n in @({names})) {{ $r = {perFeature}; \"$n=$r\" }}";
        var run = await _proc.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, ct);
        if (!run.Success) _log.LogWarning("IIS feature script failed: {Error}", run.StdErr);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in run.StdOut.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx > 0) map[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return map;
    }
}
