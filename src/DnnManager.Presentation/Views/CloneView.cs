using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Tui;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Views;

internal sealed class CloneView
{
    private readonly ConsoleScreen _screen;
    private readonly CloneProjectUseCase _useCase;
    private readonly StatusWriter _status;
    private readonly TuiProgressReporter _reporter;
    private readonly TextPrompt _text;
    private readonly ConfirmDialog _confirm;
    private readonly IFtpBrowser _ftp;
    private readonly IFtpProfileStore _profiles;
    private readonly ISqlProfileStore _sqlProfiles;
    private readonly AppOptions _options;

    public CloneView(ConsoleScreen screen, CloneProjectUseCase useCase,
        StatusWriter status, TuiProgressReporter reporter, TextPrompt text,
        ConfirmDialog confirm, IFtpBrowser ftp, IFtpProfileStore profiles,
        ISqlProfileStore sqlProfiles, IOptions<AppOptions> options)
    {
        _screen = screen; _useCase = useCase; _status = status; _reporter = reporter;
        _text = text; _confirm = confirm; _ftp = ftp; _profiles = profiles;
        _sqlProfiles = sqlProfiles;
        _options = options.Value;
    }

    private sealed record SourceKindChoice(CloneSourceKind Kind, string Label);
    private sealed record NameChoice(string Name);
    private sealed record ProfileChoice(FtpProfile? Profile, string Label);
    private sealed record NavItem(string Label, string? Subdir, bool IsCloneHere, bool IsGoUp);
    private sealed record SqlProfileChoice(SqlProfile? Profile, bool UseWebConfig, string Label);
    // Connection == null means "use whatever web.config already has".
    private sealed record SqlChoice(SiteSqlConnection? Connection);
    // Project == null means "[ New project ]".
    private sealed record ProjectPick(string? Project, string Label);
    private enum CloneAction { Full, FilesOnly, DatabaseOnly }
    private sealed record ActionChoice(CloneAction Action, string Label);

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Source kind first.
        var kindMenu = new SelectableList<SourceKindChoice>(_screen)
        {
            Title = "Clone a DNN project - choose source",
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = new[]
            {
                new SourceKindChoice(CloneSourceKind.LocalFolder, "Local folder"),
                new SourceKindChoice(CloneSourceKind.Ftp,         "FTP server"),
            },
            Display = c => c.Label
        };
        var kind = kindMenu.Show();
        if (kind is null) return; // silent cancel → back to main menu

        string target;
        CloneSource? source;
        SiteSqlConnection? dbOverride;
        var copyFiles = true;
        var seedDatabase = true;

        if (kind.Kind == CloneSourceKind.Ftp)
        {
            // 2. Choose a saved project (reuse its connections) or start a new one.
            var projects = _profiles.ListProjects();
            var picks = new List<ProjectPick>();
            foreach (var name in projects) picks.Add(new ProjectPick(name, name));
            picks.Add(new ProjectPick(null, "[ New project ]"));

            var projectMenu = new SelectableList<ProjectPick>(_screen)
            {
                Title = "FTP source - choose project",
                Hint = "↑/↓ · Enter · Esc to cancel",
                Items = picks.ToArray(),
                Display = c => c.Label
            };
            var picked = projectMenu.Show();
            if (picked is null) return;

            if (picked.Project is not null)
            {
                // Existing saved project - reuse connections and pick an action.
                target = picked.Project;
                var action = PickAction(target);
                if (action is null) return;
                copyFiles    = action is CloneAction.Full or CloneAction.FilesOnly;
                seedDatabase = action is CloneAction.Full or CloneAction.DatabaseOnly;

                var ftp = _profiles.Get(target);
                if (ftp is null)
                {
                    _screen.Clear();
                    _status.Fail($"No saved FTP connection for '{target}'.");
                    _status.Pause();
                    return;
                }
                source = new CloneSource(CloneSourceKind.Ftp, null,
                    ftp.Host, ftp.Port, ftp.User, _profiles.Unprotect(ftp.EncryptedPassword), ftp.RemotePath);

                var savedSql = _sqlProfiles.Get(target);
                dbOverride = savedSql is null
                    ? null
                    : new SiteSqlConnection(savedSql.Server, savedSql.Database, savedSql.User,
                        _sqlProfiles.Unprotect(savedSql.EncryptedPassword));
            }
            else
            {
                // New project via FTP.
                var name = AskProjectName(kind.Label);
                if (name is null) return;
                target = name;
                source = await PickFtpSourceAsync(target, ct);
                if (source is null) return;
                var sql = PickSqlCredentials(target);
                if (sql is null) return;
                dbOverride = sql.Connection;
            }
        }
        else
        {
            // Local folder source.
            var name = AskProjectName(kind.Label);
            if (name is null) return;
            target = name;
            source = PickLocalSource();
            if (source is null) return;
            var sql = PickSqlCredentials(target);
            if (sql is null) return;
            dbOverride = sql.Connection;
        }

        // Backup destination for the source DB (always auto-generated; no prompt).
        var bakPath = Path.Combine(Path.GetTempPath(),
            $"dnnmgr_clone_{target}_{DateTime.Now:yyyyMMddHHmmss}.bak");

        // Execute.
        var req = new CloneProjectRequest
        {
            TargetProjectName = target,
            Source = source,
            SourceBackupServerPath = bakPath,
            CreateIisSite = true,
            SourceDbOverride = dbOverride,
            CopyFiles = copyFiles,
            SeedDatabase = seedDatabase
        };

        _screen.Clear();
        _screen.DrawCentredTitle(1, $"Cloning \u2192 '{target}'", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        var result = await _useCase.ExecuteAsync(req, _reporter, ct);
        if (!result.Success) _status.Fail(result.Error ?? "Clone failed.");
        _status.Pause();
    }

    private string? AskProjectName(string sourceLabel)
    {
        _screen.Clear();
        _screen.DrawCentredTitle(1, $"Clone from {sourceLabel} - name the new project", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        // Re-prompt rather than bail: the name becomes a folder, an IIS site and a hostname, so
        // catching a bad one here beats failing after the user has picked a source and credentials.
        while (true)
        {
            var name = _text.Show("Project name");
            if (string.IsNullOrWhiteSpace(name)) return null; // Esc / blank = cancel

            var check = ProjectName.Validate(name);
            if (check.Success) return name;
            _status.Fail(check.Error!);
        }
    }

    // What to do when an already-saved project is selected.
    private CloneAction? PickAction(string project)
    {
        var items = new[]
        {
            new ActionChoice(CloneAction.Full,         "Clone - website files + database"),
            new ActionChoice(CloneAction.FilesOnly,    "Overwrite website files only"),
            new ActionChoice(CloneAction.DatabaseOnly, "Overwrite database only"),
        };
        var menu = new SelectableList<ActionChoice>(_screen)
        {
            Title = $"'{project}' - what do you want to do?",
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = items,
            Display = c => c.Label
        };
        return menu.Show()?.Action;
    }

    // ─── SOURCE DATABASE CREDENTIALS ───────────────────────────────────────
    private SqlChoice? PickSqlCredentials(string project)
    {
        var saved = _sqlProfiles.Get(project);

        var items = new List<SqlProfileChoice>();
        if (saved is not null)
            items.Add(new SqlProfileChoice(saved, false, $"Use saved  ({saved.User}@{saved.Server}/{saved.Database})"));
        items.Add(new SqlProfileChoice(null, true,  "[ Use credentials from web.config ]"));
        items.Add(new SqlProfileChoice(null, false, "[ New SQL connection ]"));

        var menu = new SelectableList<SqlProfileChoice>(_screen)
        {
            Title = $"Source database credentials - {project}",
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = items.ToArray(),
            Display = c => c.Label
        };
        var pick = menu.Show();
        if (pick is null) return null;                 // cancelled
        if (pick.UseWebConfig) return new SqlChoice(null);

        if (pick.Profile is not null)
        {
            var p = pick.Profile;
            return new SqlChoice(new SiteSqlConnection(
                p.Server, p.Database, p.User, _sqlProfiles.Unprotect(p.EncryptedPassword)));
        }

        // New connection.
        _screen.Clear();
        _screen.DrawCentredTitle(1, "Source database - new connection", Theme.HeaderFg);
        Console.SetCursorPosition(0, 3);

        var host = _text.Show("SQL server");
        if (string.IsNullOrWhiteSpace(host)) return null;
        var portStr = _text.Show("SQL port (blank = 1433)", allowEmpty: true);
        var port = int.TryParse(portStr, out var parsedPort) ? parsedPort : 1433;
        var dbName = _text.Show("Database name");
        if (string.IsNullOrWhiteSpace(dbName)) return null;
        var user = _text.Show("SQL user");
        if (string.IsNullOrWhiteSpace(user)) return null;
        var pwd = _text.Show("SQL password", allowEmpty: true) ?? "";

        var server = $"{host},{port}";

        // Auto-save the connection (password encrypted) under this project.
        var name = $"{user}@{server}";
        _sqlProfiles.Save(project, new SqlProfile(name, server, dbName!, user!, _sqlProfiles.Protect(pwd)));
        _status.Success($"Saved SQL profile for '{project}'.");

        return new SqlChoice(new SiteSqlConnection(server, dbName!, user!, pwd));
    }

    // ─── LOCAL ────────────────────────────────────────────────────────────
    private CloneSource? PickLocalSource()
    {
        var parent = _options.BaseDirectory;

        if (!Directory.Exists(parent))
        {
            _screen.Clear();
            _screen.DrawCentredTitle(1, "Local source", Theme.HeaderFg);
            Console.SetCursorPosition(0, 3);
            _status.Fail($"Base folder does not exist: {parent}");
            _status.Pause();
            return null;
        }

        var subs = Directory.EnumerateDirectories(parent)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NameChoice(n!))
            .ToArray();

        if (subs.Length == 0)
        {
            _screen.Clear();
            _screen.DrawCentredTitle(1, "Local source", Theme.HeaderFg);
            Console.SetCursorPosition(0, 3);
            _status.Fail($"No subfolders found in {parent}.");
            _status.Pause();
            return null;
        }

        var menu = new SelectableList<NameChoice>(_screen)
        {
            Title = $"Select source project ({parent})",
            Hint = "↑/↓ · Enter · Esc to cancel",
            Items = subs,
            Display = c => c.Name
        };
        var chosen = menu.Show();
        if (chosen is null) return null; // silent cancel

        var fullPath = Path.Combine(parent, chosen.Name);
        return new CloneSource(CloneSourceKind.LocalFolder, fullPath, null, 0, null, null, null);
    }

    // ─── FTP ──────────────────────────────────────────────────────────────
    private sealed record FtpConnection(
        string Host, int Port, string User, string Pwd, string ProfileName, string StartPath);

    private async Task<CloneSource?> PickFtpSourceAsync(string project, CancellationToken ct)
    {
        // 2a. Reuse this project's saved FTP connection or enter a new one.
        var conn = await PickFtpCredentialsAsync(project, ct);
        if (conn is null) return null; // cancelled

        // 2b. Navigate the FTP folder tree (starting at the saved folder) and pick the source.
        var remotePath = await NavigateFtpAsync(conn.Host, conn.Port, conn.User, conn.Pwd, conn.StartPath, ct);
        if (remotePath is null) return null;

        // 2c. Persist the profile (with the chosen remote directory) under this project.
        _profiles.Save(project, new FtpProfile(
            conn.ProfileName, conn.Host, conn.Port, conn.User, _profiles.Protect(conn.Pwd), remotePath));
        _status.Success($"Saved FTP profile for '{project}' ({remotePath}).");

        return new CloneSource(CloneSourceKind.Ftp, null, conn.Host, conn.Port, conn.User, conn.Pwd, remotePath);
    }

    private async Task<FtpConnection?> PickFtpCredentialsAsync(string project, CancellationToken ct)
    {
        var saved = _profiles.Get(project);
        FtpProfile? selectedProfile = null;

        if (saved is not null)
        {
            var items = new[]
            {
                new ProfileChoice(saved, $"Use saved  ({saved.User}@{saved.Host}:{saved.Port})"),
                new ProfileChoice(null,  "[ New connection ]"),
            };

            var menu = new SelectableList<ProfileChoice>(_screen)
            {
                Title = $"FTP source - {project}",
                Hint = "↑/↓ · Enter · Esc to cancel",
                Items = items,
                Display = c => c.Label
            };
            var pick = menu.Show();
            if (pick is null) return null;
            selectedProfile = pick.Profile;
        }

        string host; int port; string user; string pwd; string profileName; string startPath;

        if (selectedProfile is not null)
        {
            host = selectedProfile.Host;
            port = selectedProfile.Port;
            user = selectedProfile.User;
            pwd  = _profiles.Unprotect(selectedProfile.EncryptedPassword);
            profileName = selectedProfile.Name;
            startPath = string.IsNullOrWhiteSpace(selectedProfile.RemotePath) ? "/" : selectedProfile.RemotePath;
        }
        else
        {
            _screen.Clear();
            _screen.DrawCentredTitle(1, "FTP source - new connection", Theme.HeaderFg);
            Console.SetCursorPosition(0, 3);

            var h = _text.Show("FTP host");
            if (string.IsNullOrWhiteSpace(h)) return null;
            var portStr = _text.Show("FTP port (blank = 21)", allowEmpty: true);
            port = int.TryParse(portStr, out var p) ? p : 21;
            user = _text.Show("FTP user", allowEmpty: true) ?? "";
            pwd  = _text.Show("FTP password", allowEmpty: true) ?? "";
            host = h!;
            profileName = string.IsNullOrWhiteSpace(user) ? host : $"{user}@{host}";
            startPath = "/";
        }

        // Validate the credentials by attempting a listing of the start folder.
        _status.Info($"Connecting to {host}:{port} …");
        var probe = await _ftp.ListDirectoriesAsync(host, port, user, pwd, startPath, ct);
        if (!probe.Success)
        {
            _status.Fail(probe.Error ?? "FTP connection failed.");
            _status.Pause();
            return null;
        }
        _status.Success($"Connected to {host}.");

        return new FtpConnection(host, port, user, pwd, profileName, startPath);
    }

    private async Task<string?> NavigateFtpAsync(string host, int port, string user, string pwd, string startPath, CancellationToken ct)
    {
        var cwd = string.IsNullOrWhiteSpace(startPath) ? "/" : startPath;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var listing = await _ftp.ListDirectoriesAsync(host, port, user, pwd, cwd, ct);
            if (!listing.Success || listing.Value is null)
            {
                _status.Fail(listing.Error ?? "Failed to list FTP folder.");
                _status.Pause();
                return null;
            }

            var items = new List<NavItem>
            {
                new NavItem($"[ ✓ Clone THIS folder ({cwd}) ]", null, IsCloneHere: true, IsGoUp: false)
            };
            if (cwd != "/")
                items.Add(new NavItem("[ ← Go up ]", null, IsCloneHere: false, IsGoUp: true));
            foreach (var d in listing.Value)
                items.Add(new NavItem(d + "/", d, IsCloneHere: false, IsGoUp: false));

            var menu = new SelectableList<NavItem>(_screen)
            {
                Title = $"FTP - {host}:{cwd}",
                Hint = "↑/↓ · Enter to open · Esc to cancel",
                Items = items.ToArray(),
                Display = i => i.Label
            };
            var pick = menu.Show();
            if (pick is null) return null; // silent cancel

            if (pick.IsCloneHere) return cwd;
            if (pick.IsGoUp)
            {
                cwd = ParentPath(cwd);
                continue;
            }
            cwd = JoinPath(cwd, pick.Subdir!);
        }
    }

    private static string JoinPath(string cwd, string name)
        => cwd == "/" ? "/" + name : cwd.TrimEnd('/') + "/" + name;

    private static string ParentPath(string cwd)
    {
        if (cwd == "/" || string.IsNullOrEmpty(cwd)) return "/";
        var trimmed = cwd.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed[..idx];
    }
}
