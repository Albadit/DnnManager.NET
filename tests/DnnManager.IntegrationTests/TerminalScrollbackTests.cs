using DnnManager.Presentation.Terminal;

namespace DnnManager.IntegrationTests;

/// <summary>The terminal's scrollback keeps the newest lines, oldest first - once full, the oldest makes room.</summary>
[TestClass]
public sealed class TerminalScrollbackTests
{
    [TestMethod]
    public void AFullScrollback_KeepsTheNewestLines_InOrder()
    {
        var ring = new LineRing(3);
        for (var i = 1; i <= 5; i++) ring.Add(Line(i));

        Assert.AreEqual(3, ring.Count);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, ring.Select(l => l.Length).ToArray());
        Assert.AreEqual(5, ring[2].Length);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ring[3]);

        ring.Clear();
        Assert.AreEqual(0, ring.Count);
        ring.Add(Line(7));
        Assert.AreEqual(7, ring[0].Length);
    }

    // A line told apart by its length.
    private static Cell[] Line(int length) => new Cell[length];
}
