using System.Diagnostics;
using DnnManager.Infrastructure.Monitoring;

namespace DnnManager.IntegrationTests;

/// <summary>The worker processes' figures: memory read from the process's handle is the working set .NET reports.</summary>
[TestClass]
public sealed class ProcessSamplerTests
{
    [TestMethod]
    public void TheWorkingSet_FromTheHandle_IsTheProcesssOwn()
    {
        using var self = Process.GetCurrentProcess();
        var fromHandle = NativeMethods.WorkingSet(self.Handle);
        self.Refresh();
        var reported = self.WorkingSet64;

        Assert.IsTrue(fromHandle > 0);
        // Read a moment apart: the same within a few megabytes.
        Assert.IsTrue(Math.Abs(fromHandle - reported) < 32L * 1024 * 1024, $"{fromHandle} vs {reported}");
    }

    [TestMethod]
    public void ASample_HasTheGroupsMemory()
    {
        using var self = Process.GetCurrentProcess();
        var sampler = new ProcessSampler();
        var stats = sampler.Sample(new Dictionary<string, IReadOnlyList<int>> { ["self"] = [self.Id] });

        Assert.IsTrue(stats["self"].MemoryBytes > 0);
        Assert.IsNull(stats["self"].CpuPercent, "No CPU use before a second sample.");
    }

    [TestMethod]
    public void TheSecondSample_HasTheCpuUse_AndTheStart()
    {
        using var self = Process.GetCurrentProcess();
        using var sampler = new ProcessSampler();
        var groups = new Dictionary<string, IReadOnlyList<int>> { ["self"] = [self.Id] };
        sampler.Sample(groups);
        // Some work between the two samples.
        var until = Environment.TickCount64 + 50;
        while (Environment.TickCount64 < until) { }
        var stats = sampler.Sample(groups)["self"];

        Assert.IsNotNull(stats.CpuPercent);
        Assert.IsTrue(stats.CpuPercent >= 0);
        Assert.AreEqual(self.StartTime, stats.Started);
    }

    [TestMethod]
    public void AProcessThatEnded_IsLeftOut()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        using var sampler = new ProcessSampler();
        var groups = new Dictionary<string, IReadOnlyList<int>> { ["child"] = [child.Id] };
        Assert.IsTrue(sampler.Sample(groups).ContainsKey("child"));

        child.Kill(entireProcessTree: true);
        child.WaitForExit();

        Assert.IsFalse(sampler.Sample(groups).ContainsKey("child"), "Its handle is kept no longer than the process lives.");
    }

    [TestMethod]
    public void TheWorkerList_HasOnlyW3wp()
    {
        var list = new WorkerProcessList();
        var first = list.Read();
        var again = list.Read();
        var w3wp = Process.GetProcessesByName("w3wp").Select(p => p.Id).ToHashSet();

        // Only w3wp.exe it can open (an unelevated test may not open IIS's) - and the same when asked again.
        Assert.IsTrue(first.IsSubsetOf(w3wp));
        Assert.IsTrue(again.SetEquals(first) || again.IsSubsetOf(w3wp));
    }
}
