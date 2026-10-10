using DnnManager.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Puts saved settings to work in the running app: the Settings page saves the settings and hands them
/// here, and everything that reads <see cref="AppOptions"/> has the new values - without a restart. The
/// <c>DNNMANAGER_*</c> environment variables stay on top of them, as at the start - when what they set is allowed with
/// the new settings (<see cref="WithOverrides"/>); otherwise all of them are left out, and the activity log says why.
/// </summary>
public sealed class LiveSettings(IOptions<AppOptions> options, IConfiguration configuration, ActivityLog log)
{
    private readonly AppOptions _options = options.Value;
    private readonly IConfiguration _configuration = configuration;
    private readonly ActivityLog _log = log;

    public void Apply(UserSettings settings)
    {
        var fresh = WithOverrides(settings, _configuration, out var problems);
        if (problems.Count > 0) _log.Warn(OverridesIgnored(problems));
        _options.Apply(fresh);
    }

    /// <summary>
    /// The options of <paramref name="saved"/> with the <c>DNNMANAGER_*</c> environment variables in
    /// <paramref name="configuration"/> on top, put as the app uses them - when they are allowed by every rule of the saved
    /// settings (<see cref="AppOptions.Problems"/>): any program of the user can set them, and DNN Manager runs with
    /// administrator rights. When one isn't, none of them is used: the saved settings' options, and
    /// <paramref name="problems"/> says why.
    /// </summary>
    public static AppOptions WithOverrides(UserSettings saved, IConfiguration configuration, out IReadOnlyList<string> problems)
    {
        problems = [];
        var options = saved.ToAppOptions();
        var overrides = configuration.GetSection(AppOptions.SectionName);
        if (!overrides.Exists()) return options;
        overrides.Bind(options);
        options.Normalize();
        problems = options.Problems(saved);
        return problems.Count > 0 ? saved.ToAppOptions() : options;
    }

    /// <summary>What the start and a save say when the environment variables are left out.</summary>
    public static string OverridesIgnored(IReadOnlyList<string> problems) =>
        $"The {Program.EnvironmentPrefix}* environment variables are ignored - they set values that aren't allowed: " +
        string.Join(" ", problems.Select(p => "• " + p)) +
        " Remove or correct them (Windows Settings → System → About → Advanced system settings → Environment Variables), then restart DNN Manager.";
}
