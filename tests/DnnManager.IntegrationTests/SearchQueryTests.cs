using System.Diagnostics;
using DnnManager.Presentation.Controls;

namespace DnnManager.IntegrationTests;

/// <summary>The bottom panel's search runs on the UI thread: whatever is typed, it stops before the window would freeze.</summary>
[TestClass]
public sealed class SearchQueryTests
{
    [TestMethod]
    public void A_pattern_that_backtracks_for_ever_stops_within_its_time()
    {
        // "(a+)+b" on a line of a's with no b tries every way to split them: seconds per line.
        var query = new SearchQuery("(a+)+b", useRegex: true);
        var line = new string('a', 40);
        query.BeginPass();
        var took = Stopwatch.StartNew();
        for (var i = 0; i < 50_000; i++) query.Matches(line);
        Assert.IsTrue(took.Elapsed < TimeSpan.FromSeconds(2), $"Took {took.Elapsed}.");
        Assert.IsTrue(query.Stopped, "It didn't say it stopped.");
    }

    [TestMethod]
    public void A_new_search_starts_its_time_again()
    {
        var query = new SearchQuery("error");
        query.BeginPass();
        CollectionAssert.AreEqual(new[] { (3, 5) }, query.Matches("an error").ToArray());
        Assert.AreEqual(0, query.Matches("all fine").Count);
        Assert.IsFalse(query.Stopped);
    }
}
