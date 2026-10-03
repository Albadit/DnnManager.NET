using DnnManager.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Puts saved settings to work in the running app: the Settings page saves <c>settings.json</c> and hands the
/// settings here, and everything that reads <see cref="AppOptions"/> has the new values - without a restart.
/// </summary>
public sealed class LiveSettings(IOptions<AppOptions> options, IConfiguration configuration)
{
    private readonly AppOptions _options = options.Value;
    private readonly IConfiguration _configuration = configuration;

    public void Apply(UserSettings settings)
    {
        var fresh = settings.ToAppOptions();
        // The DNNMANAGER_* environment variables stay on top, as at startup.
        _configuration.GetSection(AppOptions.SectionName).Bind(fresh);
        _options.Apply(fresh);
    }
}