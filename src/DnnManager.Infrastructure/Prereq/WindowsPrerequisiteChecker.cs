using System.Diagnostics;
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
        if (!await prompt.ConfirmAsync($"{missing.Count} IIS feature(s) are missing - see the Output panel. Enable them now?",
                "Enable features", "Not now", true, ct))
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

    public async Task<IReadOnlyDictionary<string, bool>> GetIisFeatureStatesAsync(CancellationToken ct)
    {
        var states = await RunPerFeatureAsync(_opts.RequiredIisFeatures,
            "(Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue).State", ct);
        return _opts.RequiredIisFeatures.ToDictionary(f => f.Name,
            f => states.TryGetValue(f.Name, out var s) && s.Equals("Enabled", StringComparison.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string DockerDesktopExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");

    public async Task<DockerStatus> GetDockerStatusAsync(string containerName, CancellationToken ct)
    {
        // One call answers both "CLI there?" and "engine up?": the client version prints even when the engine is down.
        var v = await _proc.RunAsync("docker", new[] { "version", "--format", "{{.Client.Version}}|{{.Server.Version}}" }, ct);
        var parts = v.StdOut.Trim().Split('|');
        var client = parts[0].Trim();
        var server = parts.Length > 1 ? parts[1].Trim() : "";
        var installed = client.Length > 0 || File.Exists(DockerDesktopExe);
        var running = v.Success && server.Length > 0;
        if (!running)
            return new DockerStatus(installed, client.Length > 0 ? client : null, false, null, null, null);

        var c = await _proc.RunAsync("docker",
            new[] { "ps", "-a", "--filter", $"name=^{containerName}$", "--format", "{{.State}}|{{.Status}}" }, ct);
        var line = c.Success ? c.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        var container = line?.Split('|');
        return new DockerStatus(true, client, true, server,
            container is { Length: > 0 } ? container[0] : null,
            container is { Length: > 1 } ? container[1] : null);
    }

    public async Task<Result> InstallDockerDesktopAsync(IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Info("Installing Docker Desktop with winget - this downloads about 600 MB and can take several minutes…");
        var r = await _proc.RunAsync("winget",
            new[] { "install", "--id", "Docker.DockerDesktop", "-e", "--accept-package-agreements",
                    "--accept-source-agreements", "--disable-interactivity" }, ct,
            onOutput: line => { var t = line.Trim(); if (t.Length > 0) reporter.Progress(t); });
        if (r.ExitCode == -1)
            return Result.Fail("winget (App Installer) isn't available - install Docker Desktop from " +
                               "https://www.docker.com/products/docker-desktop/ instead.");
        if (!r.Success)
        {
            var output = (r.StdOut + r.StdErr).Trim();
            // winget exits non-zero when the package is already installed and there is no newer version.
            if (output.Contains("already installed", StringComparison.OrdinalIgnoreCase))
                return Result.Ok();
            return Result.Fail($"winget couldn't install Docker Desktop (exit {r.ExitCode}): " +
                               string.Join(" ", output.Split('\n').TakeLast(3).Select(l => l.Trim())));
        }
        reporter.Success("Docker Desktop installed. Start it - the first time it may ask to enable WSL 2 or to sign out and in again.");
        return Result.Ok();
    }

    public Result StartDockerDesktop()
    {
        if (!File.Exists(DockerDesktopExe)) return Result.Fail($"Docker Desktop not found at {DockerDesktopExe}.");
        try
        {
            // Through Explorer, so it runs as the signed-in user rather than elevated like DNN Manager.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DockerDesktopExe}\"") { UseShellExecute = false });
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail($"Could not start Docker Desktop: {ex.Message}");
        }
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
