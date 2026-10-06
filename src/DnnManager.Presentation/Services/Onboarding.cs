namespace DnnManager.Presentation.Services;

// The getting started guide (GuideDialog) and the help for each page - written for someone who has never used DNN
// Manager, IIS or DNN: short sentences, what a button does rather than what it is called technically, and a warning on
// everything that can't be undone. Every button and page named here exists; change the text with the UI it describes.

/// <summary>How much an action can change: shown as a badge next to it.</summary>
public enum GuideRisk
{
    None,
    /// <summary>Changes nothing, or only adds something.</summary>
    Safe,
    /// <summary>Interrupts a website for a moment, or changes it in a way that can be changed back.</summary>
    Careful,
    /// <summary>Deletes or replaces something - it can't be undone.</summary>
    Destructive
}

/// <summary>
/// One line of a guide page: an icon, a name and what it means or does. <see cref="Icon"/> is an icon resource key
/// (<c>GlyphPlay</c>, <c>FlameFilled</c>), a number (a step in a list) or <c>Dot.Running</c> / <c>Dot.Stopped</c> /
/// <c>Dot.Busy</c> / <c>Dot.None</c> (the Projects table's status dot). <c>**text**</c> in <see cref="Text"/> is bold.
/// </summary>
public sealed record GuideItem(string Icon, string Name, string Text, GuideRisk Risk = GuideRisk.None);

/// <summary>A heading on a guide page and its lines.</summary>
public sealed record GuideSection(string Heading, IReadOnlyList<GuideItem> Items);

/// <summary>
/// One page of the guide. <see cref="Since"/> is the <see cref="Onboarding.Version"/> that added it: after an update,
/// a user who went through an older guide is shown only the pages added since (see <see cref="Onboarding.AtStart"/>).
/// </summary>
public sealed record GuideStep(string Title, string Intro, IReadOnlyList<GuideSection> Sections, string? Tip = null, int Since = 1)
{
    /// <summary>The short name in the guide's step list.</summary>
    public string ShortTitle { get; init; } = Title;
}

/// <summary>What the start shows of the guide.</summary>
public enum GuideAtStart { Nothing, Welcome, NewSteps }

public static class Onboarding
{
    /// <summary>
    /// The guide's version. Raise it when a release adds a page users should see (give the page <c>Since</c> = the new
    /// version): those who saw the older guide get only the new pages at their next start - not the whole guide again.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// What to show at this start: the whole guide on DNN Manager's very first start; the pages added since the guide the
    /// user last saw; or nothing. Someone who used DNN Manager before the guide existed (<paramref name="seenVersion"/> 0
    /// but not a first start) isn't shown it - they know the app; Help opens it.
    /// </summary>
    public static (GuideAtStart What, IReadOnlyList<GuideStep> Steps) AtStart(int seenVersion, bool firstStart,
        IReadOnlyList<GuideStep> guide, int version)
    {
        if (seenVersion >= version) return (GuideAtStart.Nothing, []);
        if (seenVersion <= 0) return firstStart ? (GuideAtStart.Welcome, guide) : (GuideAtStart.Nothing, []);
        var added = guide.Where(s => s.Since > seenVersion).ToList();
        return added.Count == 0 ? (GuideAtStart.Nothing, []) : (GuideAtStart.NewSteps, added);
    }

    /// <summary>
    /// The help for a page (the ? button, F1): <c>Projects</c>, <c>Details</c> (a project's overview), <c>Setup</c>,
    /// <c>Existing</c>, <c>Settings</c> or <c>Troubleshoot</c> - the Projects page's for anything else.
    /// </summary>
    public static IReadOnlyList<GuideStep> HelpFor(string? page) => page switch
    {
        "Details" => [Details],
        "Setup" => [NewProject],
        "Existing" => [HostProject],
        "Settings" => [Settings],
        "Troubleshoot" => [Troubleshooting],
        _ => [Websites, Actions]
    };

    // ─── The guide ──────────────────────────────────────────────────────────

    public static IReadOnlyList<GuideStep> Guide => [Welcome, BeforeYouStart, Navigation, Websites, Actions, Troubleshooting, Updates, Ready];

    private static readonly GuideStep Welcome = new(
        "Welcome to DNN Manager",
        "DNN Manager runs DNN websites on this computer, so you can build and try them out before they go live. " +
        "DNN (also called DotNetNuke) is a system for building websites.",
        [
            new("With DNN Manager you can", [
                new("GlyphAdd", "Create a website", "Make a new DNN website in a few clicks. DNN Manager downloads and installs everything for you."),
                new("GlyphFolderOpen", "Bring in an existing website", "Put a copy of a website on this computer - from a backup, or from files you already have."),
                new("GlyphPlay", "Start and stop websites", "Turn websites on and off, and see at a glance which ones are running."),
                new("GlyphCopy", "Back up, copy and upgrade", "Make a backup, put a backup back, copy a website, or move it to a newer DNN version."),
                new("GlyphInfo", "Find out what is wrong", "When a website doesn't work, DNN Manager shows what happened and where to look.")
            ]),
            new("Words you will see", [
                new("GlyphList", "Project", "One website on this computer, with its files and its database."),
                new("GlyphGlobe", "IIS", "The web server that comes with Windows. It is the program that shows your websites in the browser."),
                new("GlyphDatabase", "Database", "Where a website keeps its pages, users and content."),
                new("GlyphDatabase", "Database server", "The program that looks after the databases. It has to be running for your websites to work."),
                new("GlyphContainer", "Docker", "A free program that can run a database server for you. DNN Manager can install it and set it up.")
            ])
        ],
        Tip: "This guide takes about three minutes. **Skip guide** leaves it at any time - open it again later with the **?** at the top right.")
    {
        ShortTitle = "Welcome"
    };

    private static readonly GuideStep BeforeYouStart = new(
        "Before you start",
        "DNN Manager needs a few things on this computer before it can create websites. You only do this once.",
        [
            new("Needed", [
                new("GlyphWarning", "Permission to make changes",
                    "Every time DNN Manager starts, Windows asks whether it may make changes to this computer. Click **Yes** - DNN Manager needs it to manage websites."),
                new("GlyphGlobe", "IIS, the web server",
                    "Open **Settings → IIS** and click **Test**. If something is missing, **Set up IIS** turns it on. Windows may ask you to restart the computer."),
                new("GlyphDatabase", "A database server",
                    "**Settings → Database server** chooses where websites keep their data. The standard choice, the local SQL container, uses Docker: " +
                    "open **Settings → Docker container**, click **Test** and use the button it offers - **Install Docker Desktop**, **Start Docker Desktop**, then **Set up docker-compose**."),
                new("GlyphDownload", "An internet connection",
                    "Needed to download DNN when you create a new website. Versions downloaded before can be kept for use without internet (**Settings → DNN releases**).")
            ]),
            new("Optional - change these any time", [
                new("GlyphFolder", "Projects folder", "Where the files of new websites are stored - C:\\DNN unless you choose another. **Settings → Projects**."),
                new("GlyphKeyboard", "Login of new websites",
                    "The user name and password every new website gets for its main administrator (the **host** account). Choose your own password in **Settings → Projects → DNN defaults**."),
                new("GlyphSettings", "Start with Windows", "**Settings → General → Start DNN Manager when you sign in to your computer**."),
                new("GlyphShow", "Light or dark", "The gear at the bottom left → **Themes**.")
            ])
        ],
        Tip: "Settings keeps your changes only after you click **Save**. The **Test** and **Set up** buttons use the saved settings - click **Save** first.")
    {
        ShortTitle = "Before you start"
    };

    private static readonly GuideStep Navigation = new(
        "Finding your way around",
        "The window has the same parts as many modern apps. This is what each part is for.",
        [
            new("The pages on the left", [
                new("GlyphList", "Projects", "Every website on this computer. See which ones are running, and do things with them."),
                new("GlyphAdd", "New project", "Create a new website - or make one from a backup (a .zip of its files with its database file)."),
                new("GlyphFolderOpen", "Host project",
                    "For website files that are already on this computer, for example copied from a colleague. DNN Manager sets up what is missing so the website works."),
                new("GlyphSettings", "The gear", "A menu with **Settings**, **Keyboard Shortcuts**, **Themes**, **Customize Layout**, **Troubleshoot** and **Check for Updates**.")
            ]),
            new("Along the top", [
                new("GlyphSearch", "Search", "Find a website by typing part of its name. Type **>** first to find any command instead."),
                new("GlyphQuestion", "Help", "Help for the page you are on, this guide, and a tour of the window."),
                new("GlyphDownload", "Update", "Shows only when a new version of DNN Manager is ready to install."),
                new("LayoutPanel", "Layout buttons", "Show or hide the pages on the left and the panel at the bottom.")
            ]),
            new("Along the bottom", [
                new("Dot.Running", "IIS running", "Whether the web server is on. Green means your websites can be opened."),
                new("GlyphInfo", "RAM, CPU, Disk", "How busy this computer is: memory, processor, and how full the disk is."),
                new("LayoutPanel", "The panel", "**Output**, **Logs** and **Terminal**. Open it with the panel button at the top right, or **Ctrl+J**."),
                new("GlyphCancel", "Bottom right corner", "Shows the task DNN Manager is working on, with **Cancel**, and short messages.")
            ])
        ])
    {
        ShortTitle = "Finding your way"
    };

    private static readonly GuideStep Websites = new(
        "Your websites",
        "The **Projects** page lists every DNN website on this computer, one per row. The list keeps itself up to date - there is nothing to refresh.",
        [
            new("The dot shows how a website is doing", [
                new("Dot.Running", "Running", "The website is on and can be opened in the browser."),
                new("Dot.Stopped", "Stopped",
                    "The website is off. **Start** turns it on. **App pool stopped** means its background process is off - **Start** fixes that too."),
                new("Dot.Busy", "Busy", "Starting, stopping, or a task is running on it. Wait a moment."),
                new("Dot.None", "No website", "The files are there, but IIS has no website for them.")
            ]),
            new("What the other columns tell you", [
                new("GlyphDatabase", "SQL",
                    "**Live** (green): the website's database answers. **Offline** (red): the database server can't be reached - check that it is running. **File**: the database is a file in the website's folder."),
                new("GlyphWarning", "Database: not set", "The website doesn't say which database to use. Point at it with the mouse to see why."),
                new("GlyphInfo", "DNN, CPU, Memory…", "The DNN version, how much the website uses of this computer, and when it last started. **Columns** chooses what you see."),
                new("GlyphRefresh", "Reconnecting…", "Shows at the top right when DNN Manager can't read the websites for a moment. It sorts itself out.")
            ]),
            new("What a click does", [
                new("GlyphList", "The name", "Opens the website's details: everything about it on five tabs, with buttons to change it."),
                new("GlyphGlobe", "The address", "Opens the website in your browser."),
                new("GlyphChevronRight", "The arrow", "Shows a few facts under the row."),
                new("GlyphMore", "Right-click a row", "Every action for that website, in one menu."),
                new("GlyphSearch", "Search, Columns, Only show running", "Find a website, choose the columns, or hide the websites that are stopped.")
            ])
        ])
    {
        ShortTitle = "Your websites"
    };

    private static readonly GuideStep Actions = new(
        "Buttons and what they do",
        "Each row has buttons on the right. Point at any button with the mouse to see what it does. Whatever deletes something asks first.",
        [
            new("In each row", [
                new("GlyphPlay", "Start", "Turns the website on so it can be opened in the browser.", GuideRisk.Safe),
                new("GlyphStop", "Stop", "Turns the website off. Nobody can open it until you start it again. Nothing is deleted.", GuideRisk.Careful),
                new("GlyphRefresh", "Restart",
                    "Restarts the website's background process. This can fix some problems, but the website is unavailable for a few seconds.", GuideRisk.Careful),
                new("FlameFilled", "Keep warm",
                    "Keeps the website ready, so it opens at once even after a while without visits - otherwise the first page can take up to 10 seconds. " +
                    "Works while DNN Manager runs. Orange: on. Outline: off. Grey: paused. A red dot: it is having trouble.", GuideRisk.Safe),
                new("GlyphDelete", "Remove",
                    "Deletes the website from this computer: its website in IIS, its folder and its database. Its backups are kept. Asks first, naming what goes.",
                    GuideRisk.Destructive)
            ]),
            new("Right-click a row (or ⋮ on the details)", [
                new("GlyphGlobe", "Open site / Open folder", "The website in your browser, or its files in File Explorer.", GuideRisk.Safe),
                new("GlyphFolderOpen", "Open with…", "The files in a code editor, or the database in SQL Server Management Studio - whichever are installed.", GuideRisk.Safe),
                new("GlyphList", "View logs", "What the website wrote down - the place to look when it shows an error.", GuideRisk.Safe),
                new("GlyphReset", "Clear website cache…",
                    "Deletes the website's temporary files and restarts it. Helps when pages show old content. The next page loads a little slower.", GuideRisk.Careful),
                new("GlyphKeyboard", "Rename…", "A new name for the website - its folder and address can change too. It is stopped for a moment.", GuideRisk.Careful),
                new("GlyphCopy", "Clone…", "A copy of the website under a new name. The original isn't changed.", GuideRisk.Safe),
                new("GlyphDownload", "Upgrade DNN…",
                    "Moves the website to a newer DNN version, one step at a time. A backup is made before each step and put back if a step fails.", GuideRisk.Careful),
                new("GlyphCopy", "Export", "A backup of the website (its files, its database or both), or a package to put it on a live server.", GuideRisk.Safe),
                new("GlyphReset", "Restore backup", "Puts the website back the way it was in a backup. Changes made since that backup are lost.", GuideRisk.Destructive)
            ]),
            new("Several websites at once", [
                new("GlyphCheck", "Tick the boxes",
                    "Tick the boxes in front of websites and a group of buttons appears above the list: **Start**, **Stop**, **Restart**, **Keep warm** and **Remove**. Each works on every ticked website it fits.")
            ]),
            new("While a task runs", [
                new("GlyphCancel", "Cancel",
                    "The bottom right corner shows the task. **Cancel** stops it and undoes what it did so far. One task runs at a time.", GuideRisk.Safe)
            ]),
            new("IIS, at the bottom left", [
                new("GlyphPlay", "Start IIS", "Turns the web server on, so your websites can be opened.", GuideRisk.Safe),
                new("GlyphRefresh", "Restart IIS",
                    "Every website on this computer is unavailable for a few seconds. Fixes some errors - for example after Windows parts were installed.", GuideRisk.Careful),
                new("GlyphStop", "Stop IIS", "Every website on this computer stops until you start IIS again.", GuideRisk.Careful)
            ])
        ])
    {
        ShortTitle = "Buttons and actions"
    };

    private static readonly GuideStep Troubleshooting = new(
        "When something goes wrong",
        "Logs are like a diary: DNN Manager, your websites, IIS and Windows write down what they do - and what went wrong. " +
        "You don't need them day to day, only when something doesn't work.",
        [
            new("The panel at the bottom (Ctrl+J)", [
                new("GlyphList", "Output",
                    "What DNN Manager is doing, step by step. Green **SUCCESS**: it worked. Yellow **WARN**: worth a look. Red **ERROR**: it failed - the line under it says what to do. " +
                    "A red dot on **Output** means the last task failed."),
                new("GlyphSearch", "Logs",
                    "Choose a website and a log at the top. Its logs say why it shows an error page. DNN Manager's own log is listed first. Errors are red, warnings yellow."),
                new("GlyphKeyboard", "Terminal", "A command line, for advanced users. You don't need it to use DNN Manager.")
            ]),
            new("A website doesn't work? Try these in order", [
                new("1", "Read the message", "Errors show in the bottom right corner. **Show output** opens the details."),
                new("2", "Check IIS", "The bottom left has to say **IIS running**. If it doesn't, click its **Start** button."),
                new("3", "Check the database",
                    "When the **SQL** column says **Offline**, the database server isn't running. With Docker: start Docker Desktop, or open **Settings → Docker container** and click **Test**."),
                new("4", "Restart the website", "The **Restart** button in its row."),
                new("5", "Look at its logs", "Right-click the website → **View logs**.")
            ]),
            new("Troubleshoot (the gear → Troubleshoot)", [
                new("GlyphRefresh", "Restart", "Closes DNN Manager and opens it again. Nothing is lost.", GuideRisk.Safe),
                new("GlyphDelete", "Clean up data",
                    "Deletes what you tick, to free disk space: old logs, downloaded DNN files and - only when you tick them - the project backups.", GuideRisk.Careful),
                new("GlyphReset", "Reset settings to defaults",
                    "Puts every setting back as it was when DNN Manager was installed, saved passwords too. Your websites aren't touched.", GuideRisk.Careful),
                new("GlyphWarning", "Reset to factory defaults",
                    "Removes the settings, saved passwords, logs and downloaded files, and starts DNN Manager as if it was just installed. Your websites and their backups are kept.",
                    GuideRisk.Destructive)
            ])
        ],
        Tip: "Troubleshoot never changes your websites - their files and databases stay as they are.")
    {
        ShortTitle = "Logs and troubleshooting"
    };

    private static readonly GuideStep Updates = new(
        "Updates",
        "DNN Manager looks for a new version a few seconds after it starts.",
        [
            new("How updating works", [
                new("GlyphDownload", "The Update button", "A blue **Update** button shows at the top right when a new version is ready. You don't have to install it right away."),
                new("GlyphRefresh", "When you click it",
                    "DNN Manager downloads the new version and checks that it is genuine. Then it **closes and opens again by itself**, on the page where you were. It takes about a minute."),
                new("GlyphCheck", "Your work is safe",
                    "Your websites, settings and backups aren't touched. When a task is running, DNN Manager asks before it closes. If the update fails, the old version keeps working."),
                new("GlyphInfo", "What's new", "After an update, a window shows what changed."),
                new("GlyphSearch", "Look yourself", "The gear → **Check for Updates…**, or **Settings → About**.")
            ])
        ]);

    private static readonly GuideStep Ready = new(
        "You're ready",
        "That's it. A good way to begin:",
        [
            new("Your first website", [
                new("1", "Check the settings", "**Settings → IIS** and **Settings → Database server**: click **Test**, and set up what is missing."),
                new("2", "Create a website", "**New project**: type a name and click **Create project**. The panel at the bottom shows each step."),
                new("3", "Open it", "On **Projects**, click the website's address to open it in your browser.")
            ]),
            new("Help is always close", [
                new("GlyphQuestion", "? at the top right", "Help for the page you are on (**F1**), this guide and the tour."),
                new("GlyphSettings", "Settings → Help", "This guide and the tour, whenever you want them again."),
                new("GlyphInfo", "Point at a button", "Its tooltip says what it does.")
            ])
        ],
        Tip: "Want to see where everything is? **Take the tour** points at each part of the window.")
    {
        ShortTitle = "Ready"
    };

    // ─── Help for a page ────────────────────────────────────────────────────

    private static readonly GuideStep Details = new(
        "A website's details",
        "Everything about one website, read from the website itself. **Back** (or **Esc**) returns to the list.",
        [
            new("At the top", [
                new("GlyphPlay", "Start, Stop, Restart", "The same as the buttons in its row on Projects.", GuideRisk.Careful),
                new("GlyphGlobe", "Open site / Open folder", "The website in your browser, or its files in File Explorer.", GuideRisk.Safe),
                new("GlyphMore", "⋮ More actions", "Logs, clearing the cache, export, backups and more - the same as right-clicking the website.")
            ]),
            new("The tabs", [
                new("GlyphInfo", "General", "Its folder, DNN version and backups, and a health check listing anything that is wrong."),
                new("GlyphGlobe", "IIS", "How the web server runs it: its addresses, its background process, and **Keep warm**."),
                new("GlyphList", "DNN", "The DNN installation, its portals (the sites inside it), extensions and administrator (host) accounts."),
                new("GlyphDatabase", "Database", "Where its data is, and whether the database answers."),
                new("GlyphSettings", "Advanced", "Technical settings, limits, file permissions and every problem found.")
            ]),
            new("Buttons that change the website", [
                new("GlyphKeyboard", "Rename…", "A new name - its folder and address can change too.", GuideRisk.Careful),
                new("GlyphGlobe", "Edit host names…", "The addresses the website answers on.", GuideRisk.Careful),
                new("GlyphSettings", "Edit app pool…", "How its background process runs. For advanced users.", GuideRisk.Careful),
                new("GlyphDatabase", "Change connection…", "Points the website at another database. Nothing is copied or moved.", GuideRisk.Careful),
                new("GlyphKeyboard", "Change host password…", "A new password for an administrator account. The website restarts.", GuideRisk.Careful),
                new("GlyphDownload", "Upgrade DNN…", "A newer DNN version, with a backup before each step.", GuideRisk.Careful)
            ])
        ],
        Tip: "An orange dot marks a value worth a look, a red dot a problem - the line under it says why.");

    private static readonly GuideStep NewProject = new(
        "New project",
        "Creates a website on this computer. Fill in the form from top to bottom - **Create project** can be clicked once everything is filled in correctly.",
        [
            new("The form", [
                new("1", "Project name", "Checked as you type. It becomes the name of the website's folder and part of its address."),
                new("2", "Start from",
                    "**A new site**: DNN Manager downloads DNN and installs it. **An existing site**: a .zip of the website's files with its database backup (.bacpac or .bak)."),
                new("3", "Repository and Version", "Where DNN comes from, and which version. The newest is already chosen - keep it if you're not sure."),
                new("4", "Host name and Port", "The website's address, for example mysite.dnndev.me. The suggestion is fine."),
                new("5", "DNN installation",
                    "**Automatic setup** (recommended): the website is ready on your first visit. **Manual**: DNN asks you to fill in its own setup pages."),
                new("6", "DNN account and website",
                    "The login of the website's main administrator (the host account). Write the password down - **Generate** makes a strong one."),
                new("7", "Database", "Where the website keeps its data, filled in from **Settings → Database server**. It is tested before anything is created.")
            ]),
            new("Then", [
                new("GlyphPlay", "Create project", "Starts. The panel at the bottom shows every step; **Cancel** undoes everything it did.", GuideRisk.Safe)
            ])
        ],
        Tip: "A new site needs an internet connection to download DNN.");

    private static readonly GuideStep HostProject = new(
        "Host project",
        "For a DNN website whose files are already in the projects folder - copied by hand, from git, or left from earlier. " +
        "DNN Manager only adds what is missing; the files themselves are never overwritten.",
        [
            new("Step by step", [
                new("1", "Pick a folder",
                    "Every folder in the projects folder is listed. **IIS: Live** means it already has a website, **no IIS site** that it has none. **DB** says whether its database is there and answers."),
                new("2", "Choose what to set up", "**IIS website + database** (most common), **database only**, or **IIS website only**."),
                new("3", "Backup to restore", "Optional: a database backup (.bacpac or .bak) to put in. If the database already exists, DNN Manager asks before replacing it.",
                    GuideRisk.Careful),
                new("4", "Set up", "Creates what is missing. The panel at the bottom shows each step.", GuideRisk.Safe)
            ]),
            new("Good to know", [
                new("GlyphRefresh", "Refresh", "Looks at the folders again - for a folder you just copied in."),
                new("GlyphWarning", "https:// rules",
                    "A website that sends every visitor to https:// wouldn't open on this computer, so those rules are switched off. Switch them back on before it goes live.")
            ])
        ]);

    private static readonly GuideStep Settings = new(
        "Settings",
        "Pick a category on the left, or type in the search box. Changes are kept only after **Save**; **Discard changes** undoes them. **Close** or **Esc** returns to the page.",
        [
            new("The categories", [
                new("GlyphSettings", "General", "Start with Windows, keep running when the window is closed, the size of everything, and the terminal."),
                new("GlyphFolder", "Projects", "Where websites are stored, their address, the login new websites get, and **Keep warm**."),
                new("GlyphDownload", "DNN releases", "Where DNN versions come from, and keeping downloads for use without internet."),
                new("GlyphDatabase", "Database server", "Where new websites keep their data: Docker, a SQL Server, or a file."),
                new("GlyphContainer", "Docker container", "The database server in Docker. **Test** checks it; its buttons install, start and set it up."),
                new("GlyphGlobe", "IIS", "The parts of Windows the web server needs. **Test** checks them; **Set up IIS** turns on what is missing."),
                new("GlyphKeyboard", "Keyboard shortcuts", "Every keyboard shortcut - change any of them. Saved at once."),
                new("GlyphQuestion", "Help", "The getting started guide, the tour, and the full user guide."),
                new("GlyphInfo", "About", "The version, whether an update is ready, and the folders with your files.")
            ])
        ],
        Tip: "The **Test** and **Set up** buttons use the saved settings - click **Save** first.");
}
