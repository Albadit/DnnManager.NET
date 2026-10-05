# DNN seen as a browser sees it, from the host: requests that follow redirects and keep cookies, and signing in through
# DNN's own login form (its WebForms post-back with every hidden field) - a PowerShell port of DnnManager.NET's
# DnnHttpSession (src\DnnManager.Infrastructure\Dnn\DnnHttpSession.cs). Needs PowerShell 7.

function New-DnnSession {
    param([Parameter(Mandatory)][string]$BaseUrl)
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $true
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    [pscustomobject]@{ Base = [Uri]($BaseUrl.TrimEnd('/') + '/'); Client = $client; Cookies = $handler.CookieContainer }
}

# GET or POST $Path, following redirects: returns the final status, URL, body and the hops.
function Invoke-DnnRequest {
    param(
        [Parameter(Mandatory)]$Session,
        [string]$Path = '',
        [string]$Method = 'GET',
        [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]$Form,
        [hashtable]$Headers = @{},
        [string]$Json
    )
    $uri = if ($Path -match '^https?://') { [Uri]$Path } else { [Uri]::new($Session.Base, $Path.TrimStart('/')) }
    $hops = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt 10; $i++) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $uri)
        foreach ($name in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        if ($Method -eq 'POST' -and $Form) { $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($Form) }
        if ($Method -eq 'POST' -and $Json) { $request.Content = [System.Net.Http.StringContent]::new($Json, [Text.Encoding]::UTF8, 'application/json') }
        $response = $Session.Client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($status -ge 300 -and $status -lt 400 -and $response.Headers.Location) {
            $next = if ($response.Headers.Location.IsAbsoluteUri) { $response.Headers.Location } else { [Uri]::new($uri, $response.Headers.Location) }
            $hops.Add("$Method $($uri.PathAndQuery) -> $status $($next.PathAndQuery)")
            # Stay on this site (a redirect to https or to another host name is followed here, over http).
            $uri = [Uri]::new($Session.Base, $next.PathAndQuery.TrimStart('/'))
            $Method = 'GET'; $Form = $null; $Json = $null
            continue
        }
        $hops.Add("$Method $($uri.PathAndQuery) -> $status")
        return [pscustomobject]@{ Status = $status; Uri = $uri; Html = $body; Hops = $hops }
    }
    return [pscustomobject]@{ Status = 0; Uri = $uri; Html = ''; Hops = $hops }
}

function Get-Tags([string]$html, [string]$tag) {
    [regex]::Matches($html, "<$tag\b[^>]*>", 'IgnoreCase') | ForEach-Object {
        $attributes = @{}
        foreach ($m in [regex]::Matches($_.Value, '([\w:\-\$\.]+)\s*=\s*(?:"([^"]*)"|''([^'']*)''|([^\s>"'']+))')) {
            $value = if ($m.Groups[2].Success) { $m.Groups[2].Value } elseif ($m.Groups[3].Success) { $m.Groups[3].Value } else { $m.Groups[4].Value }
            if (-not $attributes.ContainsKey($m.Groups[1].Value)) { $attributes[$m.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($value) }
        }
        $attributes
    }
}

# Signs in through DNN's login form. SignedIn: DNN set its auth cookie, didn't send the user to change the password,
# and the home page then shows a signed-in user (Persona Bar or a log-off link).
function Invoke-DnnSignIn {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$User, [Parameter(Mandatory)][string]$Password)
    $page = Invoke-DnnRequest $Session 'Login'
    $inputs = @(Get-Tags $page.Html 'input')
    $userField = ($inputs | Where-Object { $_['name'] -like '*$txtUsername' } | Select-Object -First 1)['name']
    $passwordField = ($inputs | Where-Object { $_['name'] -like '*$txtPassword' } | Select-Object -First 1)['name']
    if (-not $userField -or -not $passwordField) {
        return [pscustomobject]@{ SignedIn = $false; PersonaBar = $false; Detail = "No login form at $($page.Uri.PathAndQuery) (HTTP $($page.Status))" }
    }
    $button = Get-Tags $page.Html 'a' | Where-Object { $_['id'] -like '*_cmdLogin' } | Select-Object -First 1
    $target = if ($button -and $button['href'] -match "__doPostBack\(\s*['""]([^'""]+)['""]") { $Matches[1] } else { $userField -replace '\$txtUsername$', '$cmdLogin' }

    $form = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($input in $inputs) {
        $name = $input['name']; if (-not $name) { continue }
        $type = if ($input['type']) { $input['type'].ToLowerInvariant() } else { 'text' }
        if ($type -in 'submit', 'button', 'image', 'reset', 'file') { continue }
        if ($type -in 'checkbox', 'radio' -and -not $input.ContainsKey('checked')) { continue }
        if ($name -in '__EVENTTARGET', '__EVENTARGUMENT', $userField, $passwordField) { continue }
        $form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($name, [string]$input['value']))
    }
    foreach ($pair in @(@('__EVENTTARGET', $target), @('__EVENTARGUMENT', ''), @($userField, $User), @($passwordField, $Password))) {
        $form.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($pair[0], $pair[1]))
    }
    $action = (Get-Tags $page.Html 'form' | Select-Object -First 1)['action']
    $posted = Invoke-DnnRequest $Session $(if ($action) { [Uri]::new($page.Uri, $action).PathAndQuery } else { $page.Uri.PathAndQuery }) -Method POST -Form $form

    $cookie = @($Session.Cookies.GetCookies($Session.Base) | Where-Object Name -eq '.DOTNETNUKE').Count -gt 0
    $forced = @($posted.Hops | Where-Object { $_ -match 'PasswordReset|forced=true' }).Count -gt 0
    $homePage = Invoke-DnnRequest $Session ''
    $markers = @('personaBar-iframe', 'dnn.personaBar', 'Logoff', 'ctl=logoff' | Where-Object { $homePage.Html -match [regex]::Escape($_) })
    [pscustomobject]@{
        SignedIn = $cookie -and -not $forced -and $markers.Count -gt 0
        PersonaBar = @($markers | Where-Object { $_ -like '*personaBar*' }).Count -gt 0
        Detail = "auth cookie $(if ($cookie) { 'set' } else { 'not set' })$(if ($forced) { ', sent to change the password' }), after: $($posted.Uri.PathAndQuery), markers: $($markers -join ' ')"
        Html = $homePage.Html
    }
}

# The anti-forgery token DNN put in a page - what its Web API wants in the RequestVerificationToken header.
function Get-DnnVerificationToken([string]$html) {
    (Get-Tags $html 'input' | Where-Object { $_['name'] -eq '__RequestVerificationToken' } | Select-Object -First 1)['value']
}

Export-ModuleMember -Function New-DnnSession, Invoke-DnnRequest, Invoke-DnnSignIn, Get-DnnVerificationToken
