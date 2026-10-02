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
}
