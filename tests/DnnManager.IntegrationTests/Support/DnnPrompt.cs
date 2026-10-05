using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DnnManager.Infrastructure.Dnn;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// DNN's Persona Bar as the signed-in host: its Prompt (the command line) and its Web API - how the tests give a site
/// realistic content (users, roles, pages, modules, a second portal) through DNN's own logic rather than SQL.
/// </summary>
public sealed class DnnPrompt : IDisposable
{
    private readonly DnnHttpSession _session;
    private string? _token;
    private int _homeTab;

    private DnnPrompt(DnnHttpSession session) => _session = session;

    /// <summary>Signed in as <paramref name="host"/>; null with why when that didn't work.</summary>
    public static async Task<(DnnPrompt? Prompt, string Detail)> SignInAsync(string alias, int port, string host, string password)
    {
        var session = new DnnHttpSession(alias, port, TimeSpan.FromMinutes(5));
        var signIn = await session.SignInAsync(host, password);
        if (!signIn.SignedIn)
        {
            session.Dispose();
            return (null, signIn.Detail);
        }
        var prompt = new DnnPrompt(session);
        var home = await session.GetAsync("");
        prompt._token = DnnHttpSession.VerificationToken(home.Html);
        var tab = System.Text.RegularExpressions.Regex.Match(home.Html, @"""tabId""\s*:\s*(\d+)|TabId=(\d+)|tabid=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        prompt._homeTab = tab.Success ? int.Parse(tab.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).First(g => g.Success).Value) : 1;
        return (prompt, "signed in");
    }

    /// <summary>Runs <paramref name="command"/> (e.g. <c>new-role --name Editors</c>): DNN's answer, and whether it says it worked.</summary>
    public async Task<(bool Ok, string Answer)> RunAsync(string command) =>
        await PostAsync("API/PersonaBar/Command/Cmd", new { cmdLine = command, currentPage = _homeTab });

    /// <summary>A POST to the Persona Bar's Web API with the host's cookie and anti-forgery token: whether DNN says it worked, and its answer.</summary>
    public async Task<(bool Ok, string Answer)> PostAsync(string relative, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.Root, relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (_token is not null) request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _session.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var ok = response.IsSuccessStatusCode && !text.Contains("\"isError\":true", StringComparison.OrdinalIgnoreCase) &&
                 !text.Contains("\"Success\":false", StringComparison.OrdinalIgnoreCase);
        return (ok, $"HTTP {(int)response.StatusCode}: {(text.Length > 700 ? text[..700] + "…" : text)}");
    }

    /// <summary>A GET from the Persona Bar's Web API as the host: its answer.</summary>
    public async Task<string> GetAsync(string relative)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_session.Root, relative));
        if (_token is not null) request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _session.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public void Dispose() => _session.Dispose();
}
