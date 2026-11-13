using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DnnManager.Desktop.Services;
using DnnManager.Desktop.Views.Workbench;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views;

// Help for someone new to DNN Manager (src/DnnManager.Presentation/MainWindow.Help.cs): the getting started guide at its
// very first start (and, after an update, the pages a newer guide added), the help for the page shown (the title bar's ?,
// F1), What's new, and the tour of the window. The texts are Onboarding's (shared with the Windows app).
public partial class MainWindow
{
    private const string GuideTitle = "Getting started";

    /// <summary>Still on the version DNN Manager first started as: no release notes of changes they never saw.</summary>
    internal bool IsNewUser { get; private set; }

    private void NoteFirstVersion(bool firstStart)
    {
        var state = _workspace.Load<OnboardingState>();
        var current = _updater.Current.ToString();
        if (firstStart && state.FirstVersion is null)
        {
            state.FirstVersion = current;
            _workspace.Store.Save(state);
        }
        IsNewUser = state.FirstVersion == current;
    }

    private async Task ShowGuideAtStartAsync(bool firstStart)
    {
        var state = _workspace.Load<OnboardingState>();
        if (state.GuideVersion >= Onboarding.Version) return;
        var (what, steps) = Onboarding.AtStart(state.GuideVersion, firstStart, Onboarding.Guide, Onboarding.Version);
        var result = what switch
        {
            GuideAtStart.Welcome => await GuideDialog.OpenAsync(this, GuideTitle, steps, "Skip guide", "Start using DNN Manager", offerTour: true),
            GuideAtStart.NewSteps => await GuideDialog.OpenAsync(this, "New in DNN Manager", steps, "Close", "Got it"),
            _ => state.Skipped ? GuideResult.Skipped : GuideResult.Finished
        };
        SaveGuideSeen(result == GuideResult.Skipped);
        if (result == GuideResult.Tour) StartTour();
    }

    private void SaveGuideSeen(bool skipped)
    {
        var state = _workspace.Load<OnboardingState>();
        state.GuideVersion = Onboarding.Version;
        state.Skipped = skipped;
        _workspace.Store.Save(state);
    }

    private async Task ShowGuideAsync()
    {
        var result = await GuideDialog.OpenAsync(this, GuideTitle, Onboarding.Guide, "Skip guide", "Start using DNN Manager", offerTour: true);
        SaveGuideSeen(result == GuideResult.Skipped);
        if (result == GuideResult.Tour) StartTour();
    }

    /// <summary>The page the help is for: Settings or Troubleshoot when open, else the page - Onboarding.HelpFor's names.</summary>
    private string HelpPage => _modalKey ?? _page switch { "SourceControl" => "Deploy", var page => page };

    private static string HelpPageName(string page) => page switch { "Deploy" => "Source Control", "Setup" => "New project", _ => page };

    private Task ShowPageHelpAsync()
    {
        var page = HelpPage;
        return GuideDialog.OpenAsync(this, $"Help: {HelpPageName(page)}", Onboarding.HelpFor(page));
    }

    private Task ShowWhatsNewAsync() => WhatsNewDialog.ShowAsync(this, ReleaseNotes.Between(null, _updater.Current).Take(1).ToList());

    private static void OpenUserGuide() => Shell.Open($"{AppReleaseFeed.Repository}/blob/main/.docs/user-guide.md");

    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(MenuEntry($"Help for This Page ({HelpPageName(HelpPage)})", "help.page", () => _ = ShowPageHelpAsync()));
        menu.Items.Add(MenuEntry("Getting Started Guide", "help.gettingStarted", () => _ = ShowGuideAsync()));
        menu.Items.Add(MenuEntry("Take the Tour", "help.tour", StartTour));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Keyboard Shortcuts", "settings.keyboard", () => OpenSettings("Keyboard")));
        if (!IsNewUser) menu.Items.Add(MenuEntry("What's New in This Version", "app.whatsNew", () => _ = ShowWhatsNewAsync()));
        menu.Items.Add(MenuEntry("User Guide on GitHub", "help.userGuide", OpenUserGuide));
        menu.Open(HelpButton);
    }

    /// <summary>Settings → Help's buttons.</summary>
    private void RunHelp(string what)
    {
        switch (what)
        {
            case "guide": _ = ShowGuideAsync(); break;
            case "tour": StartTour(); break;
            case "whatsNew": _ = ShowWhatsNewAsync(); break;
            case "userGuide": OpenUserGuide(); break;
        }
    }

    // ─── The tour ───────────────────────────────────────────────────────────

    // Whether the panel was open before the tour opened it - closed again afterwards if it wasn't.
    private bool _panelOpenBeforeTour;

    private async void StartTour()
    {
        if (Tour.IsOpen || !await CloseModalAsync(focusPage: false)) return;
        if (Palette.IsOpen) Palette.Close(restoreFocus: false);
        _panelOpenBeforeTour = PanelOpen;
        Tour.Start(TourStops());
    }

    private void Tour_Ended(object? sender, bool finished)
    {
        if (!_panelOpenBeforeTour) SetPanelOpen(false);
        FocusPage();
    }

    private List<TourStop> TourStops()
    {
        var stops = new List<TourStop>();
        if (_sidebarVisible)
            stops.Add(new("The pages",
                "**Projects** lists your websites. **New project** creates one - new, or from a backup. **Source Control** compares a " +
                "project with its servers, and pushes and pulls its files.", () => SidebarTop));
        stops.Add(new("The page", "What the page in the activity bar is for. **F1** (or the **?** at the top right) says more about it.",
            () => PageCard));
        if (CommandCenter.IsVisible)
            stops.Add(new("Search everything", "Find a project by name from any page. Type **>** first to run any command instead.", () => CommandCenter));
        if (UpdateButton.IsVisible)
            stops.Add(new("A new version is ready", "**Update** installs it. DNN Manager closes and opens again by itself, where you were.", () => UpdateButton));
        stops.Add(new("Help", "Help for the page you are on (**F1**), the getting started guide, and this tour.", () => HelpButton));
        stops.Add(new("The layout", "Show or hide the side bars and the panel, or **Customize Layout** - as in VS Code.", () => CustomizeLayoutButton));
        stops.Add(new("Output and Logs",
            "**Output** shows what DNN Manager is doing, step by step. **Logs** shows what DNN Manager wrote down - look here when something goes wrong. " +
            "**Ctrl+J** opens and closes this panel.", () => PanelCard, () => SetPanelOpen(true)));
        if (_layout.StatusBarVisible)
            stops.Add(new("This computer", "How much memory, processor and disk space is in use.", () => StatusBar));
        if (_sidebarVisible)
            stops.Add(new("Settings and more",
                "**Settings**, **Themes**, **Troubleshoot** and **Check for Updates**. To begin: **Settings → IIS** and **Settings → Database server** - click **Test** in each.",
                () => ManageButton));
        return stops;
    }
}
