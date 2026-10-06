using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation;

// Help for someone new to DNN Manager: the getting started guide at its very first start (and, after an update, the
// pages a newer guide added), the help for the page shown (the title bar's ?, F1), and the tour of the window. The
// texts are in Services/Onboarding - the tour's here, next to the parts of the window they point at.
public partial class MainWindow
{
    private const string GuideTitle = "Getting started";

    /// <summary>
    /// At the start, once the workspace is back: the guide on the very first start, the pages a newer guide added, or
    /// nothing (<see cref="Onboarding.AtStart"/>). Remembered either way, so it isn't shown again.
    /// </summary>
    private void ShowGuideAtStart(bool firstStart)
    {
        var state = _workspace.Load<OnboardingState>();
        if (state.GuideVersion >= Onboarding.Version) return;
        var (what, steps) = Onboarding.AtStart(state.GuideVersion, firstStart, Onboarding.Guide, Onboarding.Version);
        var result = what switch
        {
            GuideAtStart.Welcome => GuideDialog.Open(GuideTitle, steps, "Skip guide", "Start using DNN Manager", offerTour: true),
            GuideAtStart.NewSteps => GuideDialog.Open("New in DNN Manager", steps, "Close", "Got it"),
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

    /// <summary>
    /// Still on the version DNN Manager first started as (<see cref="OnboardingState.FirstVersion"/>): What's new isn't
    /// offered - not at the start, in Help, Settings → Help or the command palette - until the first update.
    /// </summary>
    internal bool IsNewUser { get; private set; }

    /// <summary>The version of the very first start, kept for <see cref="IsNewUser"/>.</summary>
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

    /// <summary>The whole guide again - from Help, Settings → Help or the command palette.</summary>
    private void ShowGuide()
    {
        var result = GuideDialog.Open(GuideTitle, Onboarding.Guide, "Skip guide", "Start using DNN Manager", offerTour: true);
        SaveGuideSeen(result == GuideResult.Skipped);
        if (result == GuideResult.Tour) StartTour();
    }

    /// <summary>The page shown, as <see cref="Onboarding.HelpFor"/> names it - Settings or Troubleshoot when one is open over it.</summary>
    private string HelpPage => _modal switch
    {
        SettingsPage => "Settings",
        TroubleshootPage => "Troubleshoot",
        _ => PageHost.Content switch
        {
            ProjectsPage { DetailsOpen: true } => "Details",
            SetupPage => "Setup",
            ExistingFolderPage => "Existing",
            _ => "Projects"
        }
    };

    private static string HelpPageName(string page) => page switch
    {
        "Details" => "Project details",
        "Setup" => "New project",
        "Existing" => "Host project",
        _ => page
    };

    /// <summary>What the page shown is for, and what its buttons do (F1).</summary>
    private void ShowPageHelp()
    {
        var page = HelpPage;
        GuideDialog.Open($"Help: {HelpPageName(page)}", Onboarding.HelpFor(page));
    }

    private void ShowWhatsNew() => WhatsNewDialog.Show(ReleaseNotes.Between(null, _updater.Current).Take(1).ToList());

    private static void OpenUserGuide() => Shell.Open($"{AppReleaseFeed.Repository}/blob/main/.docs/user-guide.md");

    /// <summary>The title bar's ?: this page's help, the guide, the tour, and where to read more.</summary>
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = HelpButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuEntry($"Help for This Page ({HelpPageName(HelpPage)})", "help.page", ShowPageHelp));
        menu.Items.Add(MenuEntry("Getting Started Guide", "help.gettingStarted", ShowGuide));
        menu.Items.Add(MenuEntry("Take the Tour", "help.tour", StartTour));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Keyboard Shortcuts", "settings.keyboard", () => OpenSettings("Keyboard")));
        if (!IsNewUser) menu.Items.Add(MenuEntry("What's New in This Version", "app.whatsNew", ShowWhatsNew));
        menu.Items.Add(MenuEntry("User Guide on GitHub", "help.userGuide", OpenUserGuide));
        menu.IsOpen = true;
    }

    /// <summary>Settings → Help's buttons.</summary>
    private void RunHelp(string what)
    {
        switch (what)
        {
            case "guide": ShowGuide(); break;
            case "tour": StartTour(); break;
            case "whatsNew": ShowWhatsNew(); break;
            case "userGuide": OpenUserGuide(); break;
        }
    }

    // ─── The tour ───────────────────────────────────────────────────────────

    // Whether the panel was open before the tour opened it - closed again afterwards if it wasn't.
    private bool _panelOpenBeforeTour;

    /// <summary>
    /// The tour of the window. Settings or Troubleshoot close first (asking about unsaved settings); the tour shows the
    /// Projects table and opens the panel on the way, and puts the panel back as it was afterwards.
    /// </summary>
    private void StartTour()
    {
        if (Tour.IsOpen || !CloseModal(focusPage: false)) return;
        if (Palette.IsOpen) Palette.Close(restoreFocus: false);
        _panelOpenBeforeTour = LogOpen;
        Tour.Start(TourStops());
    }

    private void Tour_Ended(object? sender, bool finished)
    {
        if (!_panelOpenBeforeTour) SetLogOpen(false);
        FocusPage();
    }

    /// <summary>
    /// The tour's stops, in the order the eye goes round the window. A part hidden by the layout (the sidebar, the status
    /// bar, the title bar's search in a narrow window, Update while there is none) isn't on it.
    /// </summary>
    private List<TourStop> TourStops()
    {
        ProjectsPage? Projects() => PageHost.Content as ProjectsPage;
        void ShowTable()
        {
            NavProjects.IsChecked = true;
            if (Projects() is { DetailsOpen: true } projects) projects.CloseDetails();
        }

        var stops = new List<TourStop>();
        if (_sidebarVisible)
            stops.Add(new("The pages", "**Projects** lists your websites. **New project** creates one. **Host project** sets up website files that are already on this computer.",
                () => SidebarTop));
        stops.Add(new("Your websites",
            "One row per website. The dot shows how it is doing: **green** is running, an **empty circle** is stopped, **orange** is busy. " +
            "Click a name for everything about it; right-click a row for all its actions.",
            () => Projects()?.TourTable, ShowTable));
        stops.Add(new("Find and filter",
            "Type to find a website. **Columns** chooses what the list shows; **Only show running** hides stopped websites. " +
            "Tick rows to start, stop or remove several websites at once.",
            () => Projects()?.TourToolbar, ShowTable));
        if (CommandCenter.IsVisible)
            stops.Add(new("Search everything", "Find a website by name from any page. Type **>** first to run any command instead.", () => CommandCenter));
        if (UpdateButton.IsVisible)
            stops.Add(new("A new version is ready", "**Update** installs it. DNN Manager closes and opens again by itself, where you were.", () => UpdateButton));
        stops.Add(new("Help", "Help for the page you are on (**F1**), the getting started guide, and this tour.", () => HelpButton));
        stops.Add(new("Output, Logs and Terminal",
            "**Output** shows what DNN Manager is doing, step by step. **Logs** shows what your websites wrote down - look here when something goes wrong. " +
            "**Ctrl+J** opens and closes this panel.",
            () => PanelCard, () => SetLogOpen(true, takeKeyboard: false)));
        if (_layout.StatusBarVisible)
        {
            stops.Add(new("The web server",
                "**IIS running** in green means your websites can be opened. Its buttons restart or stop IIS - that affects **every website** on this computer.",
                () => IisStatus));
            stops.Add(new("This computer", "How much memory, processor and disk space is in use.", () => StatusBar));
        }
        if (_sidebarVisible)
            stops.Add(new("Settings and more",
                "**Settings**, **Themes**, **Troubleshoot** and **Check for Updates**. To begin: **Settings → IIS** and **Settings → Database server** - click **Test** in each.",
                () => ManageButton));
        return stops;
    }
}
