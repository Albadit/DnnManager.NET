using System.Net;
using System.Text.RegularExpressions;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// A DNN site seen through HTTP the way a browser sees it: the home page, and signing in through DNN's own login form
/// (the WebForms post-back of its login button, with every hidden field and a cookie jar).
/// </summary>
public sealed partial class DnnBrowser(Uri root)
{
    public const string AuthCookie = ".DOTNETNUKE";

    public sealed record Page(Uri Uri, int Status, string Html, IReadOnlyList<string> Hops);

    /// <summary>GET / following redirects (unless one goes to DNN's installer).</summary>
    public async Task<Page> HomeAsync()
    {
        using var http = Client(new CookieContainer());
        return await FollowAsync(http, HttpMethod.Get, root, null);
    }

    /// <summary>GET <paramref name="relative"/> (to the site's root) following every redirect - also within DNN's installer.</summary>
    public async Task<Page> PageAsync(string relative)
    {
        using var http = Client(new CookieContainer());
        return await FollowAsync(http, HttpMethod.Get, new Uri(root, relative), null, stopAtInstaller: false);
    }

    public sealed record SignIn(bool SignedIn, bool AuthCookieSet, bool ForcedPasswordReset, string FinalPath, string Detail);

    /// <summary>Signs in as <paramref name="user"/>: signed in means the auth cookie is set, DNN didn't send the user to
    /// change the password, and the home page then shows a signed-in user (the Persona Bar or a log-off link).</summary>
    public async Task<SignIn> SignInAsync(string user, string password)
    {
        var cookies = new CookieContainer();
        using var http = Client(cookies);
        var home = await FollowAsync(http, HttpMethod.Get, root, null);
        var loginLink = Anchors(home.Html).FirstOrDefault(a => a.GetValueOrDefault("id")?.Contains("loginLink", StringComparison.OrdinalIgnoreCase) == true)
            ?.GetValueOrDefault("href");
        var page = await FollowAsync(http, HttpMethod.Get, loginLink is { Length: > 0 } && !loginLink.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)
            ? new Uri(home.Uri, loginLink) : new Uri(root, "Login"), null);

        var inputs = Inputs(page.Html);
        var userField = inputs.Select(i => i.GetValueOrDefault("name")).FirstOrDefault(n => n?.EndsWith("$txtUsername", StringComparison.Ordinal) == true);
        var passwordField = inputs.Select(i => i.GetValueOrDefault("name")).FirstOrDefault(n => n?.EndsWith("$txtPassword", StringComparison.Ordinal) == true);
        if (userField is null || passwordField is null)
            return new SignIn(false, false, false, page.Uri.PathAndQuery, $"No login form on {page.Uri.PathAndQuery} (HTTP {page.Status}).");
        var button = Anchors(page.Html).FirstOrDefault(a => a.GetValueOrDefault("id")?.EndsWith("_cmdLogin", StringComparison.Ordinal) == true);
        var target = button?.GetValueOrDefault("href") is { } href && PostBack().Match(href) is { Success: true } m
            ? m.Groups[1].Value
            : userField.Replace("$txtUsername", "$cmdLogin", StringComparison.Ordinal);

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var input in inputs)
        {
            if (!input.TryGetValue("name", out var name) || name.Length == 0) continue;
            var type = input.TryGetValue("type", out var t) ? t.ToLowerInvariant() : "text";
            if (type is "submit" or "button" or "image" or "reset" or "file") continue;
            if (type is "checkbox" or "radio" && !input.ContainsKey("checked")) continue;
            fields.Add(new(name, input.GetValueOrDefault("value") ?? ""));
        }
        Set(fields, "__EVENTTARGET", target);
        Set(fields, "__EVENTARGUMENT", "");
        Set(fields, userField, user);
        Set(fields, passwordField, password);
        var action = Forms(page.Html).FirstOrDefault()?.GetValueOrDefault("action") is { Length: > 0 } a ? new Uri(page.Uri, a) : page.Uri;

        var posted = await FollowAsync(http, HttpMethod.Post, action, fields);
        var cookieSet = cookies.GetCookies(root).Any(c => c.Name == AuthCookie);
        var forced = posted.Hops.Any(h => h.Contains("PasswordReset", StringComparison.OrdinalIgnoreCase) || h.Contains("forced=true", StringComparison.OrdinalIgnoreCase));
        var onLogin = posted.Uri.AbsolutePath.Contains("/Login", StringComparison.OrdinalIgnoreCase) || posted.Html.Contains("$txtPassword", StringComparison.Ordinal);
        var markers = new List<string>();
        if (cookieSet)
        {
            var after = await FollowAsync(http, HttpMethod.Get, root, null);
            markers.AddRange(new[] { "personaBar-iframe", "dnn.personaBar", "Logoff", "Logout", "ctl=logoff" }
                .Where(marker => after.Html.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        }
        var signedIn = cookieSet && !forced && !onLogin && markers.Count > 0;
        return new SignIn(signedIn, cookieSet, forced, posted.Uri.PathAndQuery,
            $"cookie={cookieSet} forcedReset={forced} onLoginPage={onLogin} markers=[{string.Join(", ", markers)}] hops=[{string.Join(" | ", posted.Hops)}]");
    }

    private static HttpClient Client(CookieContainer cookies) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromMinutes(5) };

    private static async Task<Page> FollowAsync(HttpClient http, HttpMethod method, Uri uri, List<KeyValuePair<string, string>>? form,
        bool stopAtInstaller = true)
    {
        var hops = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (form is not null && method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(form);
            using var response = await http.SendAsync(request);
            var status = (int)response.StatusCode;
            var html = await response.Content.ReadAsStringAsync();
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                hops.Add($"{method.Method} {uri.PathAndQuery} -> {status} {Mask(next.PathAndQuery)}");
                if (stopAtInstaller && next.AbsolutePath.StartsWith("/Install", StringComparison.OrdinalIgnoreCase)) return new Page(next, status, html, hops);
                uri = next;
                method = HttpMethod.Get;
                continue;
            }
            hops.Add($"{method.Method} {Mask(uri.PathAndQuery)} -> {status}");
            return new Page(uri, status, html, hops);
        }
        throw new InvalidOperationException($"Too many redirects from {uri}.");
    }

    /// <summary>The <c>title</c> of the site logo's link - DNN's LOGO skin object puts the website's name there.</summary>
    public static string? LogoTitle(string html) =>
        Anchors(html).FirstOrDefault(a => a.GetValueOrDefault("id")?.EndsWith("dnnLOGO_hypLogo", StringComparison.OrdinalIgnoreCase) == true)
            ?.GetValueOrDefault("title");

    private static string Mask(string pathAndQuery) => ResetToken().Replace(pathAndQuery, "$1***");

    private static void Set(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        fields.RemoveAll(kv => kv.Key == name);
        fields.Add(new(name, value));
    }

    private static List<Dictionary<string, string>> Inputs(string html) => InputTag().Matches(html).Select(m => Attributes(m.Value)).ToList();
    private static List<Dictionary<string, string>> Anchors(string html) => AnchorTag().Matches(html).Select(m => Attributes(m.Value)).ToList();
    private static List<Dictionary<string, string>> Forms(string html) => FormTag().Matches(html).Select(m => Attributes(m.Value)).ToList();

    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attribute().Matches(tag))
            attributes.TryAdd(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value));
        return attributes;
    }

    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex InputTag();
    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex AnchorTag();
    [GeneratedRegex(@"<form\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex FormTag();
    [GeneratedRegex(@"([\w:\-\$\.]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))")] private static partial Regex Attribute();
    [GeneratedRegex(@"__doPostBack\(\s*['""]([^'""]+)['""]")] private static partial Regex PostBack();
    [GeneratedRegex(@"(?i)(resetToken=)[^&]+")] private static partial Regex ResetToken();
}