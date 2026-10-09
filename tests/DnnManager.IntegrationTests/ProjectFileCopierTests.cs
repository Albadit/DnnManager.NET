using System.IO.Compression;
using DnnManager.Infrastructure.Files;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>A site's files copied and zipped: every one of them, as it was.</summary>
[TestClass]
public sealed class ProjectFileCopierTests
{
    private string _run = "";

    [TestInitialize]
    public void Initialize() => _run = TestEnvironment.NewRunDirectory();

    [TestCleanup]
    public void Cleanup() => TestEnvironment.DeleteDirectory(_run);

    [TestMethod]
    public async Task A_copy_has_every_file_byte_for_byte()
    {
        var source = Path.Combine(_run, "source");
        for (var i = 0; i < 300; i++)
        {
            var folder = Path.Combine(source, $"folder{i % 7}", $"sub{i % 3}");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, $"file{i}.txt"), new string((char)('a' + i % 26), i));
        }
        var target = Path.Combine(_run, "target");

        var reporter = new RecordingReporter();
        var copied = await new ProjectFileCopier().CopyAsync(source, target, reporter, CancellationToken.None);

        Assert.IsTrue(copied.Success, copied.Error);
        var expected = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f)).Order().ToList();
        var actual = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(target, f)).Order().ToList();
        CollectionAssert.AreEqual(expected, actual);
        foreach (var file in expected)
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(source, file)), await File.ReadAllBytesAsync(Path.Combine(target, file)), file);
        StringAssert.Contains(reporter.Text, "Copied 300 files");
    }

    [TestMethod]
    public async Task A_file_older_than_zips_can_say_is_zipped_all_the_same()
    {
        var site = Path.Combine(_run, "site");
        Directory.CreateDirectory(site);
        var old = Path.Combine(site, "old.txt");
        await File.WriteAllTextAsync(old, "from long ago");
        File.SetLastWriteTime(old, new DateTime(1975, 6, 1));
        var zipPath = Path.Combine(_run, "site.zip");

        var zipped = await new ProjectFileCopier().CreateZipAsync(site, zipPath, [], new RecordingReporter(), CancellationToken.None);

        Assert.IsTrue(zipped.Success, zipped.Error);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.AreEqual(1980, zip.GetEntry("old.txt")!.LastWriteTime.Year);
    }
}
