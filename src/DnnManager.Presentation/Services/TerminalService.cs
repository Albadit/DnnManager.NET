using System.Windows.Media;
using DnnManager.Application.Configuration;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

/// <param name="Key">What <c>terminal.defaultShell</c> in settings.json stores: "powershell", "pwsh", "cmd" or "gitbash".</param>
/// <param name="ExePath">The program, e.g. <c>C:\Program Files\Git\bin\bash.exe</c>.</param>
/// <param name="Arguments">What it is started with, e.g. <c>--login -i</c>.</param>
public sealed record TerminalShell(string Key, string Name, string ExePath, string Arguments = "")
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

    private readonly Lazy<IReadOnlyList<TerminalShell>> _shells = new(FindShells);
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

    /// <summary>The shells found on this PC - Command Prompt is always there.</summary>
    public IReadOnlyList<TerminalShell> Shells => _shells.Value;

    /// <summary>The shell the settings name, or the first installed one when that one isn't (any more).</summary>
    public TerminalShell DefaultShell =>
        Shells.FirstOrDefault(s => s.Key.Equals(Settings.DefaultShell, StringComparison.OrdinalIgnoreCase)) ?? Shells[0];

    public IReadOnlyList<string> InstalledFonts => _fonts.Value;

    public FontFamily Font => new(string.IsNullOrWhiteSpace(Settings.FontFamily) ? DefaultFont : $"{Settings.FontFamily}, {DefaultFont}");

    public void Apply(TerminalSettings settings)
    {
        Settings = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenIn(string directory) => OpenRequested?.Invoke(directory);

    private static IReadOnlyList<TerminalShell> FindShells()
    {
        var shells = new List<TerminalShell>();
        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(windowsPowerShell)) shells.Add(new("powershell", "PowerShell", windowsPowerShell));

        var pwsh = OnPath("pwsh.exe") ?? FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"));
        if (pwsh is not null) shells.Add(new("pwsh", "PowerShell 7", pwsh));

        var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec)
            ? comSpec : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        shells.Add(new("cmd", "Command Prompt", cmd));

        // Git for Windows: for all users, the 32-bit one, or installed for this user only.
        var bash = FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\bin\bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\bin\bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe"));
        if (bash is not null) shells.Add(new("gitbash", "Git Bash", bash, "--login -i"));
        return shells;
    }


    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder => { try { return Path.Combine(folder, exe); } catch (ArgumentException) { return null; } })
        .FirstOrDefault(path => path is not null && File.Exists(path));
}
