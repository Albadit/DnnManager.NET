# Testing

What the tests cover, how to run them and how to add one. Building and running
the app: [development.md](development.md).

`tests\DnnManager.IntegrationTests` (MSTest) holds the tests - not part of the
app:

```bash
dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration"   # fast - no IIS, no SQL Server
dotnet test tests\DnnManager.IntegrationTests                                        # everything, about 15 minutes
```

## The fast tests

They need nothing installed and take about 40 seconds:

| File | What it checks |
|---|---|
| `InstallerUnitTests.cs` | DNN's install output and template, connection strings (never printing a password), account and password rules, DNN's password hash, the Credential Manager, settings validation, Troubleshoot's clean-up (never through a junction), undo on cancel, the log view's wrapping |
| `SetupRejectionTests.cs` | New project refuses a bad host password, a missing account, a folder that is too deep, a database it can't reach, a host name another IIS site already answers or the name of an IIS site serving another folder - before it creates anything; which site already has a host name on a port |
| `ProjectDatabaseTests.cs` | A project never destroys or quietly shares another one's database: Import gets a database (and a login) of its own whatever its zip names, Host project doesn't take over a database another IIS site uses but keeps its own, Host refuses an IIS site of its name serving another folder, Clone asks before replacing a database and copies nothing on *Keep it*, and reads the source's database from the source's web.config |
| `KeepWarmTests.cs` | Keep warm's intervals, back-off, what counts as an answer, never requesting DNN's installer, the sites kept warm and the service itself |
| `HostsFileTests.cs` | Which host names go into the hosts file (custom domains too, international ones in punycode; not wildcards or `localhost`) and to which address, the block written into a hosts file of the test's own - every other byte kept, the user's own line winning, its block found after a byte order mark, the file as it was kept beside it - and the service following the sites, not writing before IIS has been read completely |
| `SettingsRowsTests.cs` | The settings as rows in `dnnmanager.db`: a save keeps a newer version's rows and drops the list items that are gone; a new installation's own `sa` password, kept by a reset; made-up passwords meeting SQL Server's policy |
| `SettingsUserNameTests.cs` | The SQL container's login is `sqlServer.userName`, `sa` when empty; its password is saved encrypted |
| `WorkspaceStateTests.cs` | The workspace kept between starts: saved as rows, a value that can't be read keeps its default, a factory reset leaves nothing, form drafts without passwords, a window place off the screens |
| `LayoutSettingsTests.cs` | Customize Layout's choices, what is allowed, and that they are kept in the settings |
| `KeyboardTests.cs` | Shortcuts as written and allowed, kept in the settings, conflicts, and the command palette's search and recently used commands |
| `AppUpdateTests.cs` | DNN Manager's own update: the release feed, the download's checks (a release without a SHA-256 refused), the helper checking the package again (a changed one refused), its swap (never putting back an older backup) and starting the right version afterwards (one test, `Integration`, installs the newest real release) |
| `DnnReleaseOfflineTests.cs` | New project's DNN versions saved at each lookup and offered without internet |
| `WebConfigDeploymentTests.cs` | Export for deployment's `web.config`: the HTTPS rules switched off locally back on, the live connection string, debug off |
| `SiteDatabaseChecksTests.cs` | Whether a site's database is live, missing or offline - asked with its `web.config` connection, once per server and login |
| `SqlServerAddressTests.cs` | Reading a SQL Server address: which are this PC - a project's database is only dropped when it is here -, which are the local container (an address without a port only when it publishes 1433), and when two addresses are the same server |
| `IisBindingTests.cs` | The address part of IIS's binding information, IPv6 too - an edit keeps a binding's address |
| `ProjectFileCopierTests.cs` | A site's files copied (several at a time) byte for byte, and zipped with a file older than a zip can say |
| `ProjectsFolderGuardTests.cs` | The projects folder kept to administrators and you - never a drive |
| `SearchQueryTests.cs` | The panel's search stops within its time on a pattern that backtracks for ever |
| `AccessibilityTests.cs` | Names for password fields and icon buttons, the Logs view read by a screen reader, both themes with the same colours, a Contrast theme's colours, and every control that needs nothing else loading |
| `PagesLoadTests.cs` | The main window and every page, made as the app makes them - their XAML, bindings and resources - over a stand-in IIS |
| `LayeringTests.cs` | The layers' rule: Domain uses nothing of DNN Manager's, Application no Infrastructure or Presentation, Infrastructure no Presentation |
| `DailyLogFileTests.cs` | The day's log file (`dnnmanager-yyyymmdd.log`, the old name deleted) survives being deleted; warnings and errors reach it once, with their stack trace |
| `ProcessSamplerTests.cs` | Worker-process memory read from the process handle |
| `TerminalScrollbackTests.cs` | The terminal's scrollback keeps the newest lines in order |
| `ReleaseNotesTests.cs` | What's new: the release notes built in, those since the version before, and their links to the release's tag |
| `UpgradeDnnTests.cs` | Upgrade DNN and Restore backup: the upgrade package over a site but never its web.config or outside it, binding redirects following the new DLLs, what DNN's upgrade page says, a restore deleting only what was added since the backup, a backup kept from undo |
| `DnnUpgradePathTests.cs` | DNN's suggested upgrade path: the chain from any version to any target - through every listed version, a version between two listed ones to the next one first, a target between or past them, manual steps before 7.4.2, the local upgrade from 10.2 on |
| `DnnUpgradeAnalyserTests.cs` | The pre-upgrade analyser: an old SQL Server or .NET Framework, Telerik on the way to DNN 10, files and database out of step, a LocalDB file, too little space, a wrong app pool block; DNN 7 extensions, third-party extensions and custom settings warn; what couldn't be checked is said |
| `DnnUpgradeStepTests.cs` | DNN's binding-redirect merge (what its local upgrade does for each assembly - a `codeBase` kept, a redirect in a later `assemblyBinding` set there), GitHub's next page of releases, and a failure read for its likely cause |
| `AppLogTests.cs` | DNN Manager's own log on the Logs tab: its daily files newest first, its `[warning]` and `[error]` lines coloured; the hosts file listed first in a section of its own, shown whole and anew when it is rewritten in the middle |
| `InputAlignmentTests.cs` | In a search box the caret stands where the placeholder's text starts |
| `UnelevatedTests.cs` | Quoting a program's arguments; one test (`Integration`) starts a program through the desktop's shell |

## The integration tests

The integration tests (`TestCategory=Integration`) run DNN Manager's own
`SetupProjectUseCase` on a clean DNN 10.3.3 install package, with **IIS
Express** playing IIS (no administrator rights needed), and check what a
visitor sees: the home page instead of the wizard, the host signing in (a wrong
password refused), the portal and its alias, DNN's tables at the files'
version, a restart that doesn't install again, no errors in DNN's log, no
password in any message or file, and **Change host password** - once for each
kind of database (LocalDB with Windows authentication, the local SQL container,
a SQL login of its own on an empty database, a LocalDB file) -, and that
**Manual DNN setup** leaves DNN's wizard for the first visit. They need IIS
Express, SQL Server Express LocalDB and Docker Desktop; a test whose
prerequisite is missing is *inconclusive*, not failed. The package is
downloaded once into `%LOCALAPPDATA%\DnnManagerTests\cache` (or set
`DNNMANAGER_TEST_DNN_ZIP` to one you have).

`DnnUpgradeTests` upgrades real sites with DNN Manager's own
`UpgradeDnnUseCase` (see [Upgrading DNN](dnn-upgrades.md)), each taking 10 to 30
minutes:

- **The whole path:** a DNN 9.3.2 site goes through 9.13.9 and 10.2.5 to
  10.3.3. Before the upgrade it gets content through DNN's Persona Bar as the
  host: three users, a role, pages, an HTML module, a child portal, and an
  appSetting of its own. The test checks that each step passed and that
  nothing was lost, Telerik is gone, the setting is kept, the host and a user
  sign in, the child portal answers, and every step left a named backup.
- **A new project at each version:** as a user would - **New project** at
  7.4.2, 8.0.4, 9.1.1, 9.13.9 and 10.2.5, then **Upgrade DNN** to 10.3.3 through
  every step of the path. Each step must pass, and the site must end at 10.3.3
  in files and database, answer, and let the host sign in. Each row takes 3 to
  10 minutes; `DNNMANAGER_TEST_UPGRADE_FROM=7.4.2,8.0.4` runs only those.
- **A failing step:** a 9.13.9 site whose database was exported and imported
  again (its files then have SqlPackage's names, `<name>_Primary.mdf`) gets a
  database trigger that refuses DNN 10.3's version row. Step 2 must fail and
  keep its diagnostics, and the site must be put back on 10.2.5 and still work.

From the moment a test site is installed, IIS Express is stopped the way IIS
stops a site: the stop returns at once and the process ends in its own time.
A step only passes when DNN Manager waits for it, which is what DNN's lock
needs on IIS.

They need IIS Express and Docker; LocalDB isn't needed. The packages of every
version on the way are downloaded once into the cache (through GitHub's release
page when its API's rate limit is reached).

IIS Express doesn't have to be installed: without administrator rights, unpack
it into the tests' folder, and the tests use it from there (or from
`DNNMANAGER_TEST_IISEXPRESS`):

```powershell
msiexec /a iisexpress_amd64_en-US.msi /qn TARGETDIR="$env:LOCALAPPDATA\DnnManagerTests\tools\iisexpress-msi"
```

What they make is their own, and removed afterwards: a folder under
`%LOCALAPPDATA%\DnnManagerTests`, a LocalDB instance `dnnit_<id>` and a SQL
Server container `dnnit-mssql-<id>`. They never touch your projects, settings,
`MSSQLLocalDB` instance or SQL Server container.

## Adding a test

Tests go in `tests\DnnManager.IntegrationTests`. Every use case takes
pure interfaces, and the stand-ins are in its `Support\` folder
(`UntouchedIis`, `TestPrompt` - which never says yes -, `RecordingReporter`,
IIS Express as `IIisManager`…). A test that needs IIS Express, LocalDB or
Docker gets `[TestCategory("Integration")]` and is inconclusive without them.

A test that builds WPF controls runs them through `Support\WpfUi.Run`: one UI
thread for the whole run, with DNN Manager's own `App` and App.xaml's resources -
a WPF application can be made only once per process.

A test for a fixed bug should fail without the fix - check it once by undoing
the fix. Keep tests that need nothing installed out of the `Integration`
category, so the fast run covers them.
