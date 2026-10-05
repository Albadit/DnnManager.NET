using System.Windows.Threading;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The Output tab shows an operation's lines in the order they were written - also when an operation runs another inside
/// it that ends with its own "… complete" step (Upgrade DNN backs up through Export, which says "Export complete").
/// </summary>
[TestClass]
public sealed class OutputOrderTests
{
    [TestMethod]
    public void A_complete_step_inside_a_run_keeps_its_lines_where_they_happened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        try
        {
            OutputRun? run = null;
            OnUiThread(() =>
            {
                using var file = new DailyLogFile(new AppDataPaths(dir));
                var log = new ActivityLog(file, Dispatcher.CurrentDispatcher);
                var time = new DateTime(2026, 10, 5, 0, 12, 31);
                log.Clock = () => time = time.AddSeconds(1);

                log.BeginRun("Upgrade DNN of 'site' to 10.3.3");
                log.Plan(["Analyse", "10.03.03"]);
                log.Step("Analysing the site and planning the upgrade", "Analyse");
                log.Info("Current: DNN 10.02.05");
                log.Step("Step 1: 10.02.05 → 10.03.03 - backing up DNN 10.2.5", "10.03.03");
                log.Step("Zipping the website files");
                log.Info("Zipped 4.683 files");
                log.Step("Exporting the database");
                log.Info("Exported BACPAC");
                // Export's own end - inside the upgrade, not the end of it.
                log.Step("Export complete");
                log.Info("Site files: backup.zip");
                log.Warn("The zip's web.config still holds this machine's connection string");
                log.Success("Backup of DNN 10.2.5: folder");
                log.Step("Step 1: putting in DNN 10.3.3's files");
                log.Info("43 assemblies are in");
                log.Step("Upgrade complete");
                log.Success("'site' runs DNN 10.3.3");
                log.EndRun(RunStatus.Finished, null);
                Pump();
                run = log.Items.OfType<OutputRun>().Single();
            });

            var lines = run!.AllLines.ToList();
            var texts = string.Join(Environment.NewLine, lines.Select(l => $"{l.Time:HH:mm:ss} {l.Text}"));
            for (var i = 1; i < lines.Count; i++)
                Assert.IsTrue(lines[i].Time >= lines[i - 1].Time, $"Out of order at line {i}:{Environment.NewLine}{texts}");
            Assert.AreEqual(8, lines.Count, texts);
            Assert.AreEqual("'site' runs DNN 10.3.3", run.Notes.Single().Text, "Only what follows the last complete step is a closing note.");
            CollectionAssert.AreEqual(
                new[] { "Analyse", "10.03.03", "Zipping the website files", "Exporting the database", "Export complete", "putting in DNN 10.3.3's files" },
                run.Stages.Select(s => s.Name).ToArray());
            Assert.AreEqual("Upgrade complete", run.ClosingTitle);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Runs <paramref name="test"/> on a thread of its own with a dispatcher - as the app's UI thread.</summary>
    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new AssertFailedException(failure.Message, failure);
    }

    /// <summary>Everything written so far reaches the log: what was posted to the dispatcher runs.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
