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

They need nothing installed and take about 25 seconds:

| File | What it checks |
|---|---|
| `InstallerUnitTests.cs` | DNN's install output and template, connection strings (never printing a password), account and password rules, DNN's password hash, the Credential Manager, settings validation, Troubleshoot's clean-up (never through a junction), undo on cancel, the log view's wrapping |
| `SetupRejectionTests.cs` | New project refuses a bad host password, a missing account, a folder that is too deep or a database it can't reach - before it creates anything |
| `KeepWarmTests.cs` | Keep warm's intervals, back-off, what counts as an answer, never requesting DNN's installer, the sites kept warm and the service itself |
| `SettingsRowsTests.cs` | The settings as rows in `dnnmanager.db`: a save keeps a newer version's rows and drops the list items that are gone |
| `SettingsUserNameTests.cs` | The SQL container's login is `sqlServer.userName`, `sa` when empty; its password is saved encrypted |
| `WorkspaceStateTests.cs` | The workspace kept between starts: saved as rows, a value that can't be read keeps its default, a factory reset leaves nothing, form drafts without passwords, a window place off the screens |
| `LayoutSettingsTests.cs` | Customize Layout's choices, what is allowed, and that they are kept in the settings |
| `KeyboardTests.cs` | Shortcuts as written and allowed, kept in the settings, conflicts, and the command palette's search |
| `AppUpdateTests.cs` | DNN Manager's own update: the release feed, the download's checks, the helper's swap and starting the right version afterwards (one test, `Integration`, installs the newest real release) |
| `DnnReleaseOfflineTests.cs` | New project's DNN versions saved at each lookup and offered without internet |
| `WebConfigDeploymentTests.cs` | Export for deployment's `web.config`: the HTTPS rules switched off locally back on, the live connection string, debug off |
| `SiteDatabaseChecksTests.cs` | Whether a site's database is live, missing or offline - asked with its `web.config` connection, once per server and login |
| `SqlServerAddressTests.cs` | Which SQL Server addresses are this PC - a project's database is only dropped when it is here |
| `DailyLogFileTests.cs` | The day's log file survives being deleted; warnings and errors reach it once, with their stack trace |
| `ProcessSamplerTests.cs` | Worker-process memory read from the process handle |
| `TerminalScrollbackTests.cs` | The terminal's scrollback keeps the newest lines in order |
| `InputAlignmentTests.cs` | In a search box the caret stands where the placeholder's text starts |

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

A test for a fixed bug should fail without the fix - check it once by undoing
the fix. Keep tests that need nothing installed out of the `Integration`
category, so the fast run covers them.
