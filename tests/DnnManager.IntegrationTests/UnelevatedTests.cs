using DnnManager.Presentation;

namespace DnnManager.IntegrationTests;

/// <summary>Programs DNN Manager opens for the user (an editor) start as the user, through the desktop's shell.</summary>
[TestClass]
public sealed class UnelevatedTests
{
    [TestMethod]
    [DataRow(@"C:\DNN\shop", @"C:\DNN\shop")]
    [DataRow(@"C:\DNN\my shop", @"""C:\DNN\my shop""")]
    // A backslash before the closing quote is doubled, or it would take the quote for a character.
    [DataRow(@"C:\DNN\my shop\", @"""C:\DNN\my shop\\""")]
    [DataRow("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [DataRow("", "\"\"")]
    public void An_argument_is_quoted_as_the_program_reads_it_back(string argument, string quoted) =>
        Assert.AreEqual(quoted, Unelevated.Quote(argument));

    [TestMethod]
    [TestCategory("Integration")]
    public async Task A_program_starts_through_the_desktops_shell()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dnnmanager-unelevated-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.IsTrue(Unelevated.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo", "started", ">", file], null),
                "The desktop's shell (Explorer) isn't there.");
            for (var i = 0; i < 50 && !File.Exists(file); i++) await Task.Delay(100);
            Assert.IsTrue(File.Exists(file), "It didn't run.");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
