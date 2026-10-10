using System.IO.Compression;
using DnnManager.Application;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>
/// An undo that can't hang: a step that never returns is given up on after its time, a second Cancel stops the undo, and
/// what wasn't undone is said - and the restore's checks that run before anything of a site is written.
/// </summary>
[TestClass]
public sealed class ReliabilityUndoTests
{
    [TestMethod]
    public async Task AStepThatNeverReturns_IsGivenUpOn_AndTheRestStillRun()
    {
        var undo = new OperationUndo();
        var ran = false;
        undo.Add("First made", () => { ran = true; return Result.Ok(); });
        // Ignores its token, as a call into a SQL Server that doesn't answer would.
        undo.Add("Hangs", _ => new TaskCompletionSource<Result>().Task, TimeSpan.FromMilliseconds(200));
        var reporter = new RecordingReporter();

        var run = undo.RunAsync(reporter);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(run, finished, "The undo waited for a step that never returns.");
        Assert.IsFalse(await run);
        Assert.IsTrue(ran, "The step before it wasn't run.");
        StringAssert.Contains(reporter.Text, "FAIL Hangs: didn't finish within");
        StringAssert.Contains(reporter.Text, "OK   First made");
    }

    [TestMethod]
    public async Task AStepThatHonoursItsToken_IsCancelledAfterItsTime()
    {
        var undo = new OperationUndo();
        var cancelled = false;
        undo.Add("Waits", async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Result.Ok();
        }, TimeSpan.FromMilliseconds(200));

        Assert.IsFalse(await undo.RunAsync(new RecordingReporter()));
        Assert.IsTrue(cancelled, "The step's token wasn't cancelled when its time was up.");
    }

    [TestMethod]
    public async Task Skipping_StopsTheUndo_AndSaysWhatIsLeft()
    {
        var undo = new OperationUndo();
        var firstRan = false;
        undo.Add("Made first", () => { firstRan = true; return Result.Ok(); });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        undo.Add("Made last", _ =>
        {
            started.TrySetResult();
            return new TaskCompletionSource<Result>().Task;
        }, TimeSpan.FromMinutes(10));
        var reporter = new RecordingReporter();
        using var skip = new CancellationTokenSource();

        var run = undo.RunAsync(reporter, skip.Token);
        await started.Task;
        skip.Cancel();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(run, finished, "Skipping didn't stop the undo.");
        Assert.IsFalse(await run);
        Assert.IsFalse(firstRan, "A step after the skip still ran.");
        StringAssert.Contains(reporter.Text, "FAIL Made last: not done - the undo was stopped.");
        StringAssert.Contains(reporter.Text, "WARN Not undone: Made first");
    }

    [TestMethod]
    public void Pending_ListsWhatIsLeft_TheLastMadeFirst_AndChangesAreSaid()
    {
        var undo = new OperationUndo();
        var changes = 0;
        undo.Changed += () => changes++;
        undo.Add("Drop database [a]", _ => Task.FromResult(Result.Ok()));
        undo.Add("Remove the IIS site 'a'", () => Result.Ok());
        undo.CannotUndo("files copied over the folder.");

        CollectionAssert.AreEqual(
            new[] { "Remove the IIS site 'a'", "Drop database [a]", "Can't be put back: files copied over the folder." },
            undo.Pending.ToArray());
        Assert.AreEqual(3, changes);

        undo.Keep();
        Assert.AreEqual(0, undo.Pending.Count);
        Assert.AreEqual(4, changes);
    }

    [TestMethod]
    public async Task ARunThatIsCancelled_EndsItsProcess()
    {
        // ping -n 30: half a minute of a program that doesn't end by itself.
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        var cancelled = false;
        try { await new ProcessRunner().RunAsync(ping, ["-n", "30", "127.0.0.1"], cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert.IsTrue(cancelled, "The run wasn't cancelled.");
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "The cancelled program was waited for.");
        Assert.IsTrue(ChildJob.IsAvailable, "No job object to end DNN Manager's programs with it.");
    }

    [TestMethod]
    public void ARestoreZip_WithAPathOutsideTheSite_IsRefusedBeforeAnythingIsWritten()
    {
        var folder = NewFolder();
        try
        {
            var site = Directory.CreateDirectory(Path.Combine(folder, "site")).FullName;
            var zip = Path.Combine(folder, "backup.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntry("web.config");
                archive.CreateEntry("../evil.dll");
            }

            var refused = RestoreBackupUseCase.CheckZip(zip, site);

            Assert.IsNotNull(refused);
            StringAssert.Contains(refused, "../evil.dll");
            Assert.IsFalse(File.Exists(Path.Combine(folder, "evil.dll")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ARestoreZip_HoldingALink_IsRefused()
    {
        var folder = NewFolder();
        try
        {
            var site = Directory.CreateDirectory(Path.Combine(folder, "site")).FullName;
            var zip = Path.Combine(folder, "backup.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntry("web.config");
                // A symbolic link as a zip made on Linux holds one: the Unix file type in the upper bits.
                archive.CreateEntry("bin/link.dll").ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            }

            StringAssert.Contains(RestoreBackupUseCase.CheckZip(zip, site), "link");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ARestoreZip_OfOrdinaryFiles_IsAccepted()
    {
        var folder = NewFolder();
        try
        {
            var site = Directory.CreateDirectory(Path.Combine(folder, "site")).FullName;
            var zip = Path.Combine(folder, "backup.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntry("shop/web.config");
                archive.CreateEntry("shop/bin/DotNetNuke.dll");
                archive.CreateEntry("shop/Portals/0/");
            }

            Assert.IsNull(RestoreBackupUseCase.CheckZip(zip, site));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void OnlyAFileInUse_IsTriedAgain()
    {
        Assert.IsTrue(FileInUse.Is(new IOException("in use", unchecked((int)0x80070020))));
        Assert.IsTrue(FileInUse.Is(new IOException("mapped", unchecked((int)0x800704C8))));
        Assert.IsFalse(FileInUse.Is(new IOException("disk full", unchecked((int)0x80070070))));
    }

    [TestMethod]
    public async Task AFileInUse_ReachesTheRestore_SoItIsTriedAgain()
    {
        var folder = NewFolder();
        try
        {
            var site = Path.Combine(folder, "site");
            Directory.CreateDirectory(site);
            var held = Path.Combine(site, "web.config");
            File.WriteAllText(held, "the site's");
            var zip = Path.Combine(folder, "backup.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("web.config").Open()))
                writer.Write("<configuration />");

            // The worker process or bin\roslyn's compiler still has it open.
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Returned as a failed result, Restore's retry would never see it and give up at the first try.
                var ex = await Assert.ThrowsAsync<IOException>(() =>
                    new ProjectFileCopier().ExtractZipAsync(zip, site, new RecordingReporter(), CancellationToken.None));
                Assert.IsTrue(FileInUse.Is(ex), ex.Message);
            }

            var retried = await new ProjectFileCopier().ExtractZipAsync(zip, site, new RecordingReporter(), CancellationToken.None);
            Assert.IsTrue(retried.Success, retried.Error);
            Assert.AreEqual("<configuration />", File.ReadAllText(held));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
