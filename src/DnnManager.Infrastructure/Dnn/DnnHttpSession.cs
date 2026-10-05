using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// A DNN site on this PC seen as a browser sees it - following redirects, keeping cookies, and signing in through DNN's
/// own login form (its WebForms post-back, with every hidden field). Every request goes to 127.0.0.1 on the site's port
/// with the site's host name, so it doesn't depend on the host name resolving. The upgrade's checks use it.
/// </summary>
internal sealed partial class DnnHttpSession : IDisposable
{
    public const string AuthCookie = ".DOTNETNUKE";

    private readonly Uri _root;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    /// <param name="alias">The <c>host[:port][/child]</c> to ask for, e.g. <c>shop.dnndev.me</c>.</param>
    /// <param name="port">The port the site's binding listens on, on this PC.</param>
    public DnnHttpSession(string alias, int port, TimeSpan timeout)
    {
        _root = new Uri($"http://{alias.TrimEnd('/')}/");
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = _cookies, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(20),
            // This PC, whatever the host name resolves to.
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        })
        { Timeout = timeout };
    }

    public sealed record Page(Uri Uri, int Status, string Html, IReadOnlyList<string> Hops)
    {
        public bool AtInstaller => Uri.AbsolutePath.StartsWith("/Install", StringComparison.OrdinalIgnoreCase);
        public bool AtLogin => Uri.AbsolutePath.Contains("/Login", StringComparison.OrdinalIgnoreCase) ||
                               Uri.Query.Contains("ctl=login", StringComparison.OrdinalIgnoreCase);
        public bool AtErrorPage => Uri.AbsolutePath.Contains("ErrorPage", StringComparison.OrdinalIgnoreCase) ||
                                   Uri.Query.Contains("error=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GET <paramref name="relative"/> (to the site's root), following redirects - stopping at DNN's installer.</summary>
    public Task<Page> GetAsync(string relative) => FollowAsync(HttpMethod.Get, new Uri(_root, relative.TrimStart('/')), null);

    public sealed record SignIn(bool SignedIn, bool PersonaBar, string Detail);

    /// <summary>
    /// Signs in as <paramref name="user"/>: signed in when DNN set its auth cookie, didn't send the user to change the
    /// password, and the home page then shows a signed-in user. <see cref="SignIn.PersonaBar"/>: it shows DNN's Persona Bar.
    /// </summary>
    public async Task<SignIn> SignInAsync(string user, string password)
    {
        var home = await GetAsync("");
        var loginLink = Anchors(home.Html).FirstOrDefault(a => a.GetValueOrDefault("id")?.Contains("loginLink", StringComparison.OrdinalIgnoreCase) == true)
            ?.GetValueOrDefault("href");
        var page = await FollowAsync(HttpMethod.Get, loginLink is { Length: > 0 } && !loginLink.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)
            ? new Uri(home.Uri, loginLink) : new Uri(_root, "Login"), null);

        var inputs = Inputs(page.Html);
        var userField = inputs.Select(i => i.GetValueOrDefault("name")).FirstOrDefault(n => n?.EndsWith("$txtUsername", StringComparison.Ordinal) == true);
        var passwordField = inputs.Select(i => i.GetValueOrDefault("name")).FirstOrDefault(n => n?.EndsWith("$txtPassword", StringComparison.Ordinal) == true);
        if (userField is null || passwordField is null)
            return new SignIn(false, false, $"No login form on {page.Uri.PathAndQuery} (HTTP {page.Status}).");
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

        var posted = await FollowAsync(HttpMethod.Post, action, fields);
        var cookieSet = _cookies.GetCookies(_root).Any(c => c.Name == AuthCookie);
        var forced = posted.Hops.Any(h => h.Contains("PasswordReset", StringComparison.OrdinalIgnoreCase) || h.Contains("forced=true", StringComparison.OrdinalIgnoreCase));
        var onLogin = posted.AtLogin || posted.Html.Contains("$txtPassword", StringComparison.Ordinal);
        var markers = new List<string>();
        if (cookieSet)
        {
            var after = await GetAsync("");
            markers.AddRange(new[] { "personaBar-iframe", "dnn.personaBar", "Logoff", "Logout", "ctl=logoff" }
                .Where(marker => after.Html.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        }
        var signedIn = cookieSet && !forced && !onLogin && markers.Count > 0;
        return new SignIn(signedIn, markers.Any(k => k.Contains("personaBar", StringComparison.OrdinalIgnoreCase)),
            $"auth cookie {(cookieSet ? "set" : "not set")}{(forced ? ", sent to change the password" : "")}{(onLogin ? ", back on the login page" : "")}, " +
            $"after: {posted.Uri.PathAndQuery}");
    }

    private async Task<Page> FollowAsync(HttpMethod method, Uri uri, List<KeyValuePair<string, string>>? form)
    {
        var hops = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (form is not null && method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(form);
            using var response = await _http.SendAsync(request);
            var status = (int)response.StatusCode;
            var html = await response.Content.ReadAsStringAsync();
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                // Somewhere else than this site (another host name): followed on this PC all the same, under that name.
                hops.Add($"{method.Method} {uri.PathAndQuery} -> {status} {Mask(next.PathAndQuery)}");
                if (next.AbsolutePath.StartsWith("/Install", StringComparison.OrdinalIgnoreCase)) return new Page(next, status, html, hops);
                uri = next.Scheme == Uri.UriSchemeHttps ? new UriBuilder(next) { Scheme = Uri.UriSchemeHttp, Port = _root.Port }.Uri : next;
                method = HttpMethod.Get;
                continue;
            }
            hops.Add($"{method.Method} {Mask(uri.PathAndQuery)} -> {status}");
            return new Page(uri, status, html, hops);
        }
        return new Page(uri, 0, "", [.. hops, "too many redirects"]);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The site's root as requested (<c>http://alias/</c>).</summary>
    public Uri Root => _root;

    /// <summary>A request in this session - its cookies (signed in, once <see cref="SignInAsync"/> did), on this PC.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request);

    /// <summary>The anti-forgery token DNN put in a page (<c>__RequestVerificationToken</c>) - what its Web API wants in a header.</summary>
    public static string? VerificationToken(string html) =>
        Inputs(html).FirstOrDefault(i => i.GetValueOrDefault("name") == "__RequestVerificationToken")?.GetValueOrDefault("value");

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
