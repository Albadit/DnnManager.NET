using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>What's new: the release notes built into DNN Manager, those since the version before, and where their links go.</summary>
[TestClass]
public sealed class ReleaseNotesTests
{
    [TestMethod]
    public void TheNotesSinceTheVersionBefore_AreShown_NewestFirst()
    {
        var notes = ReleaseNotes.Between(new Version(1, 7, 0), new Version(1, 7, 2));

        CollectionAssert.AreEqual(new[] { new Version(1, 7, 2), new Version(1, 7, 1) }, notes.Select(n => n.Version).ToArray());
        StringAssert.StartsWith(notes[0].Markdown, "# DNN Manager 1.7.2");
    }

    [TestMethod]
    public void EveryReleaseHasItsNotes_BuiltIn()
    {
        var notes = ReleaseNotes.Between(null, new Version(1, 7, 1));

        Assert.AreEqual(new Version(1, 7, 1), notes[0].Version, "Not a newer one than asked for.");
        Assert.IsTrue(notes.Any(n => n.Version == new Version(1, 0, 0)), "The first release's too.");
        Assert.AreEqual(0, ReleaseNotes.Between(new Version(1, 7, 1), new Version(1, 7, 1)).Count, "Nothing new on the same version.");
    }

    [TestMethod]
    public void ARelativeLink_GoesToTheFileAtTheReleasesTag()
    {
        var note = new ReleaseNote(new Version(1, 7, 3), "");

        Assert.AreEqual("https://github.com/Albadit/DnnManager.NET/blob/v1.7.3/CHANGELOG.md#v173",
            note.LinkTarget("../../CHANGELOG.md#v173"));
        Assert.AreEqual("https://example.com/a", note.LinkTarget("https://example.com/a"));
    }
}
