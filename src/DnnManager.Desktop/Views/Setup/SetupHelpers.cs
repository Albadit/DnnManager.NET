using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>
/// The DNN releases of each repository in the settings, asked of GitHub once and kept for the rest of the run - the
/// Windows app's DnnReleaseCatalog (Presentation/Services), which isn't in this app's services yet: one for the app,
/// made on first use. A failed lookup isn't kept, nor one answered from the releases saved for offline use, so the
/// next request (New project shown again, another repository picked) tries GitHub again.
/// </summary>
public sealed class SetupReleaseCatalog(IServiceProvider services)
{
    private static SetupReleaseCatalog? _shared;
    private readonly Dictionary<string, Task<Result<DnnReleaseList>>> _lists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's one catalog, over <see cref="Program.Services"/>.</summary>
    public static SetupReleaseCatalog Shared => _shared ??= new SetupReleaseCatalog(Program.Services);

    /// <summary>Starts loading every repository's releases, without waiting for them.</summary>
    public void Preload()
    {
        foreach (var api in services.GetRequiredService<IDnnReleaseService>().KnownReleaseApis)
            _ = GetAsync(api);
    }

    /// <summary>The releases of <paramref name="api"/>, highest version first - from the earlier lookup when it reached GitHub.</summary>
    public Task<Result<DnnReleaseList>> GetAsync(string api)
    {
        lock (_lists)
        {
            if (_lists.TryGetValue(api, out var known)) return known;
            var lookup = LoadAsync(api);
            _lists[api] = lookup;
            _ = lookup.ContinueWith(done =>
            {
                if (done.Result is { Success: true, Value.SavedAt: null }) return;
                lock (_lists)
                    if (_lists.TryGetValue(api, out var current) && current == done) _lists.Remove(api);
            }, TaskScheduler.Default);
            return lookup;
        }
    }

    private async Task<Result<DnnReleaseList>> LoadAsync(string api)
    {
        try
        {
            // IDnnReleaseService is a typed HttpClient (transient) - a fresh one for each lookup.
            return await services.GetRequiredService<IDnnReleaseService>().ListReleasesAsync(api, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Result<DnnReleaseList>.Fail(ex.Message);
        }
    }
}

/// <summary>What the New project page and its cards share: names, error lines, the status words' colours.</summary>
internal static class SetupText
{
    /// <summary>"Deutsch (de-DE)" - the language's own name and its code; the code alone when it isn't known (Pages/Languages.cs).</summary>
    public static string LanguageName(string culture)
    {
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            return info.NativeName.Length == 0 ? culture : $"{char.ToUpper(info.NativeName[0])}{info.NativeName[1..]} ({culture})";
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    /// <summary><c>https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases</c> → <c>dnnsoftware/Dnn.Platform</c>.</summary>
    public static string RepositoryLabel(string api)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return api;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "repos" ? $"{parts[1]}/{parts[2]}" : api;
    }

    /// <summary>
    /// A field's problem in its error line, shown only when there is one - and the help text of the field it is about,
    /// so a screen reader hears it there too (the Windows app's FieldError).
    /// </summary>
    public static void ShowError(TextBlock line, Control? field, string text)
    {
        line.Text = text;
        line.IsVisible = text.Length > 0;
        if (field is not null) AutomationProperties.SetHelpText(field, text.Length > 0 ? text : null);
    }

    /// <summary>The status words' tones (StatusTone in the Windows app's DockerCard).</summary>
    public enum Tone { Good, Bad, Warning, Muted }

    /// <summary>Colours a status word and fills in its detail.</summary>
    public static void SetStatus(TextBlock status, TextBlock detail, string text, Tone tone, string detailText)
    {
        status.Text = text;
        status.Classes.Set("good", tone == Tone.Good);
        status.Classes.Set("badTone", tone == Tone.Bad);
        status.Classes.Set("warnTone", tone == Tone.Warning);
        detail.Text = detailText;
    }

    /// <summary>Opens an address in the browser; a failure is said in a message.</summary>
    public static async Task OpenAsync(Control from, string url)
    {
        var opened = false;
        try
        {
            if (TopLevel.GetTopLevel(from) is { } top && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                opened = await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        if (!opened) await Services.Dialogs.MessageAsync($"Could not open {url}.");
    }
}
