using DnnManager.Infrastructure.Sql;

namespace DnnManager.IntegrationTests;

/// <summary>
/// A site's LocalDB file is opened in a LocalDB of the file's own version: with Visual Studio's LocalDB 2019 next to 2025,
/// the user's MSSQLLocalDB (2019) can't open the file the site's instance (2025) made - "version 998. This server
/// supports version 904 and earlier" - and a newer one would upgrade a file beyond what the site's instance opens.
/// </summary>
[TestClass]
public sealed class LocalDbVersionTests
{
    [TestMethod]
    public void The_file_is_opened_in_the_LocalDB_of_its_own_version()
    {
        int[] both = [15, 17];
        Assert.AreEqual(17, LocalDbVersions.MajorToOpen(998, both), "A file the site's 2025 instance made: 2025, not the user's 2019.");
        Assert.AreEqual(15, LocalDbVersions.MajorToOpen(904, both), "A 2019 file isn't upgraded to 2025 behind the site's back.");
        Assert.AreEqual(15, LocalDbVersions.MajorToOpen(611, both), "DNN's shipped file (SQL Server 2005): the oldest that opens it.");
        Assert.AreEqual(17, LocalDbVersions.MajorToOpen(611, [17]));
        Assert.IsNull(LocalDbVersions.MajorToOpen(998, [15]), "Only 2019 here: nothing can open a 2025 file.");
        Assert.AreEqual(18, LocalDbVersions.MajorToOpen(1100, [17, 18]), "A LocalDB newer than the list opens anything.");
    }

    [TestMethod]
    public void A_file_version_names_the_LocalDB_that_made_it()
    {
        Assert.AreEqual(17, LocalDbVersions.MajorFor(998));
        Assert.AreEqual(15, LocalDbVersions.MajorFor(904));
        Assert.AreEqual(11, LocalDbVersions.MajorFor(611));
        Assert.AreEqual(18, LocalDbVersions.MajorFor(1100));
    }

    [TestMethod]
    public void The_version_is_read_from_the_file_header()
    {
        var file = Path.Combine(Path.GetTempPath(), $"DnnManagerLocalDb-{Guid.NewGuid():N}.mdf");
        try
        {
            // dbi_version at 0x12064, as SQL Server writes it - 998 for a file LocalDB 2025 made.
            var bytes = new byte[0x12100];
            BitConverter.GetBytes((short)998).CopyTo(bytes, 0x12064);
            File.WriteAllBytes(file, bytes);
            Assert.AreEqual(998, LocalDbVersions.FileVersionOf(file));

            File.WriteAllBytes(file, new byte[100]);
            Assert.IsNull(LocalDbVersions.FileVersionOf(file), "Too short for a header: not a version.");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
