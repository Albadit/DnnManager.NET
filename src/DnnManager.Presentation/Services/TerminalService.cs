using System.Windows.Media;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Pages.Projects;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

//// <param name="Key">What <c>terminal.defaultShell</c> in the settings stores: "powershell", "pwsh", "cmd" or "gitbash".</param>
/// <param name="ExePath">The program, e.g. <c>C:\Program Files\Git\bin\bash.exe</c>.</param>
/// <param name="Arguments">What it is started with, e.g. <c>-NoProfile</c>.</param>
/// <param name="AsAdministrator">
/// Started with DNN Manager's administrator rights - otherwise as the signed-in user, without them (the default).
/// </param>
/// <param name="Refusal">
/// Why it isn't offered as an Administrator terminal (others could change the program) - shown greyed with this; null
/// when it can be started.
/// </param>
public sealed record TerminalShell(string Key, string Name, string ExePath, string Arguments = "", bool AsAdministrator = false,
    string? Refusal = null)
{
    public string CommandLine => Arguments.Length == 0 ? $"\"{ExePath}\"" : $"\"{ExePath}\" {Arguments}";
}

/// <summary>
/// What the terminal panel and the Settings page share: the terminal's settings as they are now (the page changes them
/// while the app runs), the shells installed on this PC and the fonts to choose from - and a way for any page to open
/// a terminal in a folder.
/// </summary>
public sealed class TerminalService
{
    /// <summary>The font when none is chosen - and the fallback behind a chosen one.</summary>
    public const string DefaultFont = "Cascadia Mono, Consolas, Courier New";

    // Fixed-width fonts worth offering; only the installed ones are listed.
    private static readonly string[] FontCandidates =
    [
        "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New", "Lucida Console", "JetBrains Mono", "Fira Code",
        "Source Code Pro", "Hack", "DejaVu Sans Mono"
    ];

    private readonly Lazy<(IReadOnlyList<TerminalShell> User, IReadOnlyList<TerminalShell> Administrator)> _shells = new(FindShells);
    private readonly Lazy<IReadOnlyList<string>> _fonts = new(() =>
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return FontCandidates.Where(installed.Contains).ToList();
    });

    private readonly AppOptions _options;

    public TerminalService(IOptions<AppOptions> options)
    {
        _options = options.Value;
        Settings = _options.Terminal;
    }

    public TerminalSettings Settings { get; private set; }

    /// <summary>Where a new shell starts: the projects folder, as the settings have it now.</summary>
    public string WorkingDirectory => _options.BaseDirectory;

    /// <summary><see cref="Settings"/> were changed on the Settings page.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>A page asks for a terminal in a folder - the window opens the panel with a new shell there.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>A page asks for a site's logs - the window opens the panel on its Logs tab with that log (the newest when null).</summary>
    internal event Action<ProjectRow, SiteLogSource?>? LogsRequested;

    /// <summary>
    /// The shells found on this PC, started as the signed-in user (without administrator rights) - Command Prompt is
    /// always there. The user's own installs count (PowerShell 7 or Git for the user only, on their PATH).
    /// </summary>
    public IReadOnlyList<TerminalShell> Shells => _shells.Value.User;

    /// <summary>
    /// The shells for an Administrator terminal: each one found, those others could change greyed with
    /// <see cref="TerminalShell.Refusal"/> - Command Prompt is always one that can be started.
    /// </summary>
    public IReadOnlyList<TerminalShell> AdministratorShells => _shells.Value.Administrator;

    /// <summary>The shell the settings name, or the first installed one when that one isn't (any more).</summary>
    public TerminalShell DefaultShell =>
        Shells.FirstOrDefault(s => s.Key.Equals(Settings.DefaultShell, StringComparison.OrdinalIgnoreCase)) ?? Shells[0];

    /// <summary>The default shell for an Administrator terminal - the settings' one when it may be, otherwise Command Prompt.</summary>
    public TerminalShell DefaultAdministratorShell =>
        AdministratorShells.FirstOrDefault(s => s.Refusal is null && s.Key.Equals(Settings.DefaultShell, StringComparison.OrdinalIgnoreCase))
        ?? AdministratorShells.First(s => s.Refusal is null);

    public IReadOnlyList<string> InstalledFonts => _fonts.Value;

    public FontFamily Font => new(string.IsNullOrWhiteSpace(Settings.FontFamily) ? DefaultFont : $"{Settings.FontFamily}, {DefaultFont}");

    public void Apply(TerminalSettings settings)
    {
        Settings = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenIn(string directory) => OpenRequested?.Invoke(directory);

    internal void ShowLogs(ProjectRow site, SiteLogSource? source = null) => LogsRequested?.Invoke(site, source);

    /// <remarks>
    /// A terminal starts as the signed-in user - without DNN Manager's administrator rights: any shell they have, with
    /// their profile and PATH. An Administrator terminal is asked for on its own; it runs with those rights, so only a
    /// shell nobody else can change (one in the user's own folders, or on their PATH there, could be swapped by any
    /// program of theirs) - and none of the scripts a shell runs as it starts from the user's own folders (a PowerShell
    /// profile in Documents, cmd's AutoRun, ~/.bashrc), which any program of theirs could change too.
    /// </remarks>
    private static (IReadOnlyList<TerminalShell> User, IReadOnlyList<TerminalShell> Administrator) FindShells()
    {
        var user = new List<TerminalShell>();
        var administrator = new List<TerminalShell>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        void Add(string key, string name, IEnumerable<string> userCandidates, IEnumerable<string> adminCandidates, string userArguments,
            string adminArguments)
        {
            if (userCandidates.FirstOrDefault(File.Exists) is { } exe) user.Add(new(key, name, exe, userArguments));
            // The first copy that may run as Administrator - or, when none may, the first found, greyed with why.
            TerminalShell? refused = null;
            foreach (var candidate in adminCandidates.Where(File.Exists))
            {
                var resolved = TrustedPrograms.Resolve(candidate);
                if (resolved.Path is { } path)
                {
                    administrator.Add(new(key, name, path, adminArguments, AsAdministrator: true));
                    return;
                }
                refused ??= new(key, name, candidate, adminArguments, AsAdministrator: true, Refusal: resolved.Problem(name));
            }
            if (refused is not null) administrator.Add(refused);
        }

        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        Add("powershell", "PowerShell", [windowsPowerShell], [windowsPowerShell], "", "-NoProfile");

        var allUsersPwsh = Path.Combine(programFiles, @"PowerShell\7\pwsh.exe");
        // As the user: theirs first (on their own PATH - a per-user or Store install), then the one for all users.
        Add("pwsh", "PowerShell 7", OnUserPath("pwsh.exe").Append(allUsersPwsh),
            // As Administrator: on the computer's PATH, then Program Files - only a copy administrators alone can change.
            TrustedPrograms.OnMachinePath("pwsh.exe").Append(allUsersPwsh), "", "-NoProfile");

        // Windows' own, whatever %ComSpec% says; /d as Administrator: no AutoRun command from the registry.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Add("cmd", "Command Prompt", [cmd], [cmd], "", "/d");

        // Git for Windows: for all users, the 32-bit one, or installed for this user only.
        string[] bash =
        [
            Path.Combine(programFiles, @"Git\bin\bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\bin\bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe")
        ];
        Add("gitbash", "Git Bash", bash, bash, "--login -i", "--noprofile --norc -i");
        return (user, administrator);
    }

    // The user's PATH as this process has it (the computer's and theirs): for a shell that runs as them.
    private static IEnumerable<string> OnUserPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder => { try { return Path.IsPathFullyQualified(folder) ? Path.Combine(folder, exe) : null; } catch (ArgumentException) { return null; } })
        .OfType<string>();
}
