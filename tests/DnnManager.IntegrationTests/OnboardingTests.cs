using System.Runtime.ExceptionServices;
using System.Windows;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.State;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Themes;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The getting started guide: shown on DNN Manager's very first start only, never again once seen or skipped, and after
/// an update only the pages a newer guide added. Its help covers every page, and every icon it names exists.
/// </summary>
[TestClass]
public sealed class OnboardingTests
{
    private static GuideStep Step(string title, int since = 1) => new(title, "intro", []) { Since = since };

    private static readonly IReadOnlyList<GuideStep> Guide = [Step("Welcome"), Step("Buttons"), Step("Ready")];

    [TestMethod]
    public void The_first_start_shows_the_whole_guide()
    {
        var (what, steps) = Onboarding.AtStart(seenVersion: 0, firstStart: true, Guide, version: 1);
        Assert.AreEqual(GuideAtStart.Welcome, what);
        CollectionAssert.AreEqual(Guide.ToList(), steps.ToList());
    }

    [TestMethod]
    public void A_guide_seen_or_skipped_is_not_shown_again()
    {
        Assert.AreEqual(GuideAtStart.Nothing, Onboarding.AtStart(1, firstStart: false, Guide, 1).What);
        // A factory reset forgets the workspace: that start is a first one again - but with the guide still remembered
        // (an older build's reset), it stays seen.
        Assert.AreEqual(GuideAtStart.Nothing, Onboarding.AtStart(1, firstStart: true, Guide, 1).What);
    }

    [TestMethod]
    public void Someone_who_used_DNN_Manager_before_the_guide_existed_is_not_shown_it()
    {
        Assert.AreEqual(GuideAtStart.Nothing, Onboarding.AtStart(0, firstStart: false, Guide, 1).What);
    }

    [TestMethod]
    public void A_newer_guide_shows_only_the_pages_added_since()
    {
        IReadOnlyList<GuideStep> guide = [Step("Welcome"), Step("Backups", since: 2), Step("Ready"), Step("Cloud", since: 3)];

        var (what, steps) = Onboarding.AtStart(seenVersion: 1, firstStart: false, guide, version: 3);
        Assert.AreEqual(GuideAtStart.NewSteps, what);
        CollectionAssert.AreEqual(new[] { "Backups", "Cloud" }, steps.Select(s => s.Title).ToArray());

        Assert.AreEqual("Cloud", Onboarding.AtStart(2, false, guide, 3).Steps.Single().Title);
        // A raised version without a page of its own shows nothing.
        Assert.AreEqual(GuideAtStart.Nothing, Onboarding.AtStart(3, false, guide, 4).What);
    }

    [TestMethod]
    public void Whether_the_guide_was_seen_is_kept_for_the_next_start()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DnnManagerOnboardingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.AreEqual(0, new StateStore(new AppDatabase(dir)).Load<OnboardingState>().GuideVersion, "Never shown: 0.");
            new StateStore(new AppDatabase(dir)).Save(new OnboardingState { GuideVersion = Onboarding.Version, Skipped = true });
            var read = new StateStore(new AppDatabase(dir)).Load<OnboardingState>();
            Assert.AreEqual(Onboarding.Version, read.GuideVersion);
            Assert.IsTrue(read.Skipped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* SQLite may hold the file for a moment */ }
        }
    }

    [TestMethod]
    public void Every_page_has_help()
    {
        foreach (var page in new[] { "Projects", "Details", "Setup", "Existing", "Settings", "Troubleshoot" })
            Assert.IsTrue(Onboarding.HelpFor(page).Count > 0, page);
        CollectionAssert.AreEqual(Onboarding.HelpFor("Projects").ToList(), Onboarding.HelpFor("anything else").ToList());
    }

    [TestMethod]
    public void Every_page_of_the_guide_has_a_title_text_and_lines()
    {
        foreach (var step in Onboarding.Guide.Concat(new[] { "Details", "Setup", "Existing", "Settings" }.SelectMany(Onboarding.HelpFor)))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Title));
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Intro), step.Title);
            Assert.IsTrue(step.Sections.Count > 0 && step.Sections.All(s => s.Items.Count > 0), step.Title);
            Assert.IsTrue(step.Since is >= 1 and <= Onboarding.Version, $"{step.Title}: Since {step.Since}");
            // Bold is **…**: an odd number of markers would make the rest of the text bold.
            foreach (var text in step.Sections.SelectMany(s => s.Items).Select(i => i.Text).Append(step.Intro).Append(step.Tip ?? ""))
                Assert.AreEqual(0, (text.Split("**").Length - 1) % 2, $"{step.Title}: unbalanced ** in \"{text}\"");
        }
    }

    [TestMethod]
    public void Every_icon_the_guide_names_is_in_the_theme() => Sta(() =>
    {
        var icons = new Icons();
        var items = Onboarding.Guide.Concat(new[] { "Details", "Setup", "Existing", "Settings" }.SelectMany(Onboarding.HelpFor))
            .SelectMany(s => s.Sections).SelectMany(s => s.Items);
        foreach (var item in items)
        {
            if (item.Icon.All(char.IsAsciiDigit) || item.Icon.StartsWith("Dot.", StringComparison.Ordinal)) continue;
            Assert.IsTrue(icons.Contains(item.Icon), $"{item.Name}: no icon {item.Icon} in Themes/Icons.xaml");
        }
    });

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // The dictionaries' pack URIs need WPF's application parts.
                if (System.Windows.Application.Current is null) _ = new System.Windows.Application();
                body();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
