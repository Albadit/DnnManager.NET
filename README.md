# DNN Manager

**Run DNN sites on your Windows PC without the IIS and SQL Server chores.**

DNN Manager is a desktop app for DNN (DotNetNuke) developers. It creates a
working DNN site in one step and then manages every site in your IIS from a
single window - start, stop, logs, terminal, database - in the style of Docker
Desktop.

[**Download**](https://github.com/Bond-for-web-solutions/DnnManager.NET/releases/latest) ·
[User guide](docs/user-guide.md) ·
[Development](docs/development.md) ·
[Architecture](docs/architecture.md) ·
[Changelog](CHANGELOG.md)

## Why

A local DNN site by hand means: download a release, unzip it, create an IIS
site and app pool, set folder permissions, create a database and a login, edit
`web.config` and click through DNN's install wizard. And once it runs, IIS puts
it to sleep after 20 minutes, so the next page takes half a minute again.

DNN Manager does all of that for you - and undoes it just as easily: removing a
project takes its IIS site, folder and database with it.

## What you get

- **A new site in one click** - pick a DNN version from GitHub, give it a name,
  **Create project**. The first visit shows your site, signed in as the host
  account you chose - no install wizard.
- **Every IIS site in one table** - live status, CPU, memory, DNN version and
  database. Changes made in IIS Manager show up by themselves, no Refresh.
- **Start, stop, restart or remove** one site or several at once.
- **Keep warm** - no more slow first page: chosen sites are kept awake with a
  tiny request every few minutes.
- **Import, export and clone** sites as `.zip` + `.bacpac`, also from Azure SQL.
- **Host an existing folder** - the IIS site and database for code you already
  have.
- **A bottom panel like VS Code's** - Output, live DNN / IIS / Windows logs, and
  terminals (PowerShell, Command Prompt, Git Bash) opened in a project's folder.
- **Open with…** Visual Studio, VS Code, Rider, Cursor and others, or SQL Server
  Management Studio signed in to the site's database.
- **Your choice of database** - a SQL Server container it sets up in Docker for
  you, your own SQL Server / SQL Server Express, or a LocalDB file.
- **Light and dark theme**, UI scaling, and an efficiency mode that keeps the
  app idle while you aren't looking at it.

## Quick start

1. Download `DnnManagerSetup-<version>-x64.exe` from
   [Releases](https://github.com/Bond-for-web-solutions/DnnManager.NET/releases/latest)
   and run it - installing needs no administrator rights.
2. Start **DNN Manager** and accept the UAC prompt - it manages IIS, so it runs
   as Administrator.
3. Open **Settings** (the gear): **IIS → Set up IIS** enables the Windows
   features DNN needs, and **Docker container → Set up docker-compose** creates
   the SQL Server container. Using your own SQL Server instead? Pick it under
   **Database server**.
4. Click **New project**, enter a name, pick a DNN version and press **Create
   project**. When it's done, open `http://<name>.dnndev.me` - `dnndev.me`
   points to your own PC.

The [user guide](docs/user-guide.md) walks through every page, menu and setting.

## Requirements

- Windows 10 / 11 or Windows Server, with IIS (DNN Manager can enable it)
- An account that can run programs as Administrator
- For the default database: Docker Desktop with Linux containers - or any SQL
  Server, SQL Server Express or LocalDB instead

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/Bond-for-web-solutions/DnnManager.NET.git
cd DnnManager.NET
dotnet run                    # builds and starts the app (asks for admin rights)
dotnet test tests/DnnManager.IntegrationTests --filter "TestCategory!=Integration"   # fast tests
```

Debugging, the conventions and how to extend it are in
[docs/development.md](docs/development.md); the tests in
[docs/testing.md](docs/testing.md); publishing and the installer in
[docs/releasing.md](docs/releasing.md).

## How it's built

A WPF app on .NET 10, laid out as Clean Architecture in a single project:

| Layer | What it holds |
|---|---|
| `DnnManager.Presentation` | WPF windows, pages, themes and the app's start-up |
| `DnnManager.Application` | Use cases (new project, import, clone, remove…) and the interfaces they need |
| `DnnManager.Infrastructure` | IIS, Docker, SQL Server, GitHub releases, settings, keep warm, logs |
| `DnnManager.Domain` | Plain records, no dependencies |

Domain and Application don't depend on WPF or Infrastructure; use cases reach
IIS, SQL Server and the file system through interfaces. The layers, how an
operation runs, how the window stays current and a map from each feature to its
code are in [docs/architecture.md](docs/architecture.md).

## Documentation

| | |
|---|---|
| For | Document | What's in it |
|---|---|---|
| Users | [User guide](docs/user-guide.md) | The window, every page and menu, automatic DNN setup, import / host / clone, keep warm, backups, limitations |
| | [Configuration](docs/configuration.md) | `Documents\DnnManager` and its database, every settings key, environment variables |
| | [Troubleshooting](docs/troubleshooting.md) | Known problems, their causes and fixes |
| Developers | [Development](docs/development.md) | Prerequisites, build, run, debugging, conventions, extending |
| | [Architecture](docs/architecture.md) | Layers, how an operation runs, live updates, project layout, design decisions, component map |
| | [Upgrading DNN](docs/dnn-upgrades.md) | DNN's upgrade path, the analyser, backups, each step, the checks, rollback, the knowledge base, what was tested |
| | [Testing](docs/testing.md) | The fast and the integration tests, adding a test |
| | [Releasing](docs/releasing.md) | Version, changelog, portable exe, installer, GitHub release |
| | [Security](docs/security.md) | Administrator rights, secrets, what DNN Manager deletes, network exposure, open risks |
| Everyone | [Changelog](CHANGELOG.md) | What changed in each version |
| | [Release notes](docs/release-notes/) | The notes of every GitHub release, one file per version |

## Contributing

Issues and pull requests are welcome. Before opening a pull request, run the
fast tests, add a line to [CHANGELOG.md](CHANGELOG.md) under *Unreleased*, and
update the docs when behaviour changes. Follow the conventions in
[docs/development.md](docs/development.md#conventions).

## License

[MIT](LICENSE). The terminal icons and keep warm's flame are
[Codicons](https://github.com/microsoft/vscode-codicons) by Microsoft, licensed
under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
