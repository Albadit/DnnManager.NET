using DnnManager.Application.Upgrades;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>DNN's upgrade path exported to a file and imported from one (Settings → DNN releases → Edit upgrade path).</summary>
[TestClass]
public sealed class UpgradePathFileTests
{
    private static IReadOnlyList<DnnPathVersion> Imported(string json)
    {
        var read = UpgradePathFile.Parse(json);
        Assert.IsTrue(read.Success, read.Error);
        return read.Value!;
    }

    [TestMethod]
    public void An_exported_path_imports_as_it_was()
    {
        var path = DnnUpgradePath.PathWith(null).Where(v => v.Version != new Version(10, 2, 5))
            .Append(new DnnPathVersion(new Version(10, 4, 2), 533320, 16, "Read the notes – “quoted”."))
            .ToList();
        CollectionAssert.AreEqual(path.OrderBy(v => v.Version).ToList(), Imported(UpgradePathFile.ToJson(path)).ToList());
    }

    [TestMethod]
    public void Versions_DNN_Manager_listed_after_the_file_was_made_are_kept()
    {
        // Written by a DNN Manager that knew the path up to 9.13.9: what this one lists after it wasn't taken off.
        const string json = """
            {
              "kind": "dnnmanager.upgradePath",
              "knownUpTo": "9.13.9",
              // By hand: comments and a trailing comma are fine.
              "path": [ { "version": "9.13.9" }, { "version": "09.03.02", "note": "Ours." }, ]
            }
            """;
        var path = Imported(json);
        CollectionAssert.AreEqual(new[] { "09.03.02", "09.13.09", "10.02.05", "10.03.03" }, path.Select(v => DnnUpgradeStep.Name(v.Version)).ToArray());
        Assert.AreEqual(DnnUpgradePath.Listed(new Version(10, 3, 3)), path[^1]);
        Assert.AreEqual("Ours.", path[0].Note);
    }

    [TestMethod]
    [DataRow("", "isn't an upgrade path")]
    [DataRow("not json", "isn't an upgrade path")]
    [DataRow("""{ "path": [ { "version": "10.4.2" } ] }""", "isn't an upgrade path")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [] }""", "no versions")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ { "version": "10.4.2; calc" } ] }""", "isn't a DNN version")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ { "version": "10.4.2" }, { "version": "10.04.02" } ] }""", "twice")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ { "version": "10.4.2", "sqlServerMajor": -1 } ] }""", "isn't a version")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ { "version": "10.4.2", "note": "two\nlines" } ] }""", "one line")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ { "version": "7.0.0" } ] }""", "older than 7.4.2")]
    [DataRow("""{ "kind": "dnnmanager.upgradePath", "path": [ null ] }""", "isn't a DNN version")]
    public void A_file_that_isnt_a_usable_path_is_refused(string json, string why)
    {
        var read = UpgradePathFile.Parse(json);
        Assert.IsFalse(read.Success);
        StringAssert.Contains(read.Error, why);
    }

    [TestMethod]
    public void A_file_too_big_to_be_a_path_isnt_read()
    {
        var file = Path.Combine(Path.GetTempPath(), $"upgrade-path-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, new string(' ', (int)UpgradePathFile.MaxBytes + 1));
            StringAssert.Contains(UpgradePathFile.Read(file).Error, "too big");
            Assert.IsTrue(UpgradePathFile.Write(file, DnnUpgradePath.PathWith(null)).Success);
            Assert.AreEqual(DnnUpgradePath.Versions.Count, UpgradePathFile.Read(file).Value!.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
