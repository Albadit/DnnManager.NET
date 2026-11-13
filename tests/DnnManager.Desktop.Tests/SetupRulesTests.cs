using DnnManager.Application.Abstractions;
using DnnManager.Desktop.Views.Setup;

namespace DnnManager.Desktop.Tests;

/// <summary>The New project page's rules that draw nothing - no headless session needed (one per process: SourceControlViewTests').</summary>
[TestClass]
public sealed class SetupRulesTests
{
    [TestMethod]
    public void A_releases_api_is_shown_as_owner_and_repository()
    {
        Assert.AreEqual("dnnsoftware/Dnn.Platform", SetupText.RepositoryLabel("https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases"));
        Assert.AreEqual("not a url", SetupText.RepositoryLabel("not a url"), "What isn't an address is shown as it is.");
        Assert.AreEqual("https://example.com/x", SetupText.RepositoryLabel("https://example.com/x"), "Not GitHub's /repos/owner/name: as it is.");
    }

    [TestMethod]
    public void A_language_is_its_own_name_and_its_code()
    {
        StringAssert.EndsWith(SetupText.LanguageName("de-DE"), "(de-DE)");
        StringAssert.StartsWith(SetupText.LanguageName("de-DE"), "D");
        Assert.AreEqual("no culture!", SetupText.LanguageName("no culture!"), "What isn't a culture name: the code alone.");
    }

    [TestMethod]
    public void The_iis_features_dialog_says_what_is_wrong_with_its_rows()
    {
        Assert.IsNull(IisFeaturesDialog.FindProblem([("IIS Web Server", "IIS-WebServerRole"), ("", "IIS-ASPNET45")]));
        Assert.AreEqual("Fill in or remove the empty row.", IisFeaturesDialog.FindProblem([("", "")]));
        Assert.AreEqual("'ASP.NET' needs its Windows feature name.", IisFeaturesDialog.FindProblem([("ASP.NET", "")]));
        Assert.AreEqual("'IIS ASPNET' isn't a Windows feature name: letters, digits, '-', '_' and '.' only.",
            IisFeaturesDialog.FindProblem([("", "IIS ASPNET")]));
        Assert.AreEqual("iis-aspnet is there twice.", IisFeaturesDialog.FindProblem([("", "IIS-ASPNET"), ("", "iis-aspnet")]),
            "Names are compared without case, as Windows does.");
    }

    [TestMethod]
    public void A_check_line_is_marked_by_its_outcome()
    {
        var failed = new CheckLine("Server reachable", "No answer", CheckOutcome.Failed);
        Assert.IsTrue(failed.IsFailed);
        Assert.IsFalse(failed.IsWarning);
        Assert.IsTrue(new CheckLine("Login", "ok", CheckOutcome.Warning).IsWarning);
    }
}
