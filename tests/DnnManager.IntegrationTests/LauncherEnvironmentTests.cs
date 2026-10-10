using DnnManager.Infrastructure.Startup;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The launcher (src/DnnManager.Launcher): the environment it starts DNN Manager with, where it is looked for, and what the
/// sign-in task runs.
/// </summary>
[TestClass]
public sealed class LauncherEnvironmentTests
{
    [TestMethod]
    public void Every_dotnet_variable_is_dropped_whatever_its_case_and_the_rest_is_kept()
    {
        var environment = new System.Collections.Hashtable
        {
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER"] = "{11111111-1111-1111-1111-111111111111}",
            ["CORECLR_PROFILER_PATH_64"] = @"C:\Users\x\evil.dll",
            ["COR_PROFILER"] = "{22222222-2222-2222-2222-222222222222}",
            ["cor_enable_profiling"] = "1",
            ["DOTNET_DiagnosticPorts"] = @"\\.\pipe\evil",
            ["DOTNET_EnableEventPipe"] = "1",
            ["dotnet_eventpipeoutputpath"] = @"C:\Windows\System32\evil.nettrace",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\Users\x\hook.dll",
            ["COMPlus_EnableDiagnostics"] = "1",
            ["complus_JitStdOutFile"] = @"C:\x.txt",
            ["Path"] = @"C:\Windows\system32",
            ["USERPROFILE"] = @"C:\Users\x",
            ["DNNMANAGER_DnnManager__SitePort"] = "8080",
            // Only the prefix counts: these merely contain the letters.
            ["MY_DOTNET_TOOLS"] = "kept",
            ["CORE"] = "kept"
        };

        var clean = LaunchEnvironment.Clean(environment);

        CollectionAssert.AreEquivalent(
            new[] { "Path", "USERPROFILE", "DNNMANAGER_DnnManager__SitePort", "MY_DOTNET_TOOLS", "CORE", "DOTNET_EnableDiagnostics" },
            clean.Keys.ToArray());
        Assert.AreEqual("0", clean["DOTNET_EnableDiagnostics"], "Diagnostics are off - set, not only left out.");
        Assert.AreEqual(@"C:\Windows\system32", clean["PATH"], "Names compare without case, as Windows' do.");
    }

    [TestMethod]
    public void Diagnostics_stay_off_even_when_the_environment_turns_them_on()
    {
        var clean = LaunchEnvironment.Clean(new System.Collections.Hashtable { ["DOTNET_EnableDiagnostics"] = "1", ["dotnet_enablediagnostics"] = "1" });

        Assert.AreEqual(1, clean.Count);
        Assert.AreEqual("0", clean["DOTNET_EnableDiagnostics"]);
    }

    [TestMethod]
    [DataRow("DOTNET_gcServer", true)]
    [DataRow("Complus_ZapDisable", true)]
    [DataRow("CORECLR_PROFILER", true)]
    [DataRow("COR_PROFILER_PATH", true)]
    [DataRow("DOTNETX", false)]
    [DataRow("CORE_SOMETHING", false)]
    [DataRow("TEMP", false)]
    public void What_is_dropped_is_every_variable_of_dotnets_prefixes(string name, bool dropped) =>
        Assert.AreEqual(dropped, LaunchEnvironment.IsDropped(name));

    [TestMethod]
    public void The_launcher_is_used_only_beside_an_installed_DnnManager_exe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var app = Path.Combine(dir, "DnnManager.exe");
            File.WriteAllText(app, "");
            Assert.IsNull(LaunchEnvironment.LauncherBeside(app), "No launcher there: DNN Manager starts as before.");

            var launcher = Path.Combine(dir, LaunchEnvironment.LauncherFileName);
            File.WriteAllText(launcher, "");
            Assert.AreEqual(launcher, LaunchEnvironment.LauncherBeside(app));
            Assert.IsNull(LaunchEnvironment.LauncherBeside(Path.Combine(dir, "DnnManager_Portable-1.9.0-x64.exe")),
                "A portable exe isn't the one the launcher starts.");
            Assert.IsNull(LaunchEnvironment.LauncherBeside(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void The_sign_in_task_starts_the_launcher_and_counts_as_starting_the_exe_beside_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var app = Path.Combine(dir, "DnnManager.exe");
            var launcher = Path.Combine(dir, LaunchEnvironment.LauncherFileName);
            File.WriteAllText(app, "");
            Assert.AreEqual(app, StartupTask.CommandFor(app), "Without a launcher, the exe itself (a portable DNN Manager).");

            File.WriteAllText(launcher, "");
            Assert.AreEqual(launcher, StartupTask.CommandFor(app));
            Assert.AreEqual(app, StartupTask.AppOf(launcher), "Settings shows the DNN Manager it starts.");
            Assert.AreEqual(app, StartupTask.AppOf(app), "A task made by 1.8.1 or older starts the exe itself.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
