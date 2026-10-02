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
| `InstallerUnitTests.cs` | DNN's install output and template, connection strings (never printing a password), account and password rules, DNN's password hash, the Credential Manager, settings validation and migration, Troubleshoot's clean-up (never through a junction), undo on cancel, the log view's wrapping |
| `SetupRejectionTests.cs` | New project refuses a bad host password, a missing account, a folder that is too deep or a database it can't reach - before it creates anything |
| `KeepWarmTests.cs` | Keep warm's intervals, back-off, what counts as an answer, never requesting DNN's installer, the per-site records and the service itself |
| `SqlServerAddressTests.cs` | Which SQL Server addresses are this PC - a project's database is only dropped when it is here |
| `DailyLogFileTests.cs` | The day's log file survives being deleted; warnings and errors reach it once, with their stack trace |
| `SettingsUserNameTests.cs` | The SQL container's login is `sqlServer.userName`, `sa` when empty |
| `ProcessSamplerTests.cs` | Worker-process memory read from the process handle |
| `TerminalScrollbackTests.cs` | The terminal's scrollback keeps the newest lines in order |

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
