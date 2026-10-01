using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>How a new project's DNN gets installed.</summary>
public enum DnnInstallMode
{
    /// <summary>DNN Manager runs DNN's own unattended install: the first visit shows the new site.</summary>
    Automatic,

    /// <summary>DNN's installation wizard, filled in by the user on the first visit - DNN's normal behaviour.</summary>
    Manual
}

/// <summary>
/// What DNN Manager remembers about a project it set up - what can't be read back from the site itself.
/// </summary>
/// <param name="Site">The IIS site's name.</param>
/// <param name="DnnVersion">The DNN release installed, e.g. "10.3.3".</param>
/// <param name="HostUserName">The host account an automatic install created; null for a manual install.</param>
public sealed record ProjectRecord(string Site, DnnInstallMode InstallMode, DateTime CreatedUtc, string? DnnVersion, string? HostUserName);

/// <summary>The <see cref="ProjectRecord"/>s, one file per project in <c>Documents\DnnManager\projects</c>.</summary>
public interface IProjectRecords
{
    /// <summary>The record of site <paramref name="site"/>, or null when DNN Manager didn't set it up (or before it kept records).</summary>
    ProjectRecord? Find(string site);

    void Save(ProjectRecord record);

    void Remove(string site);
}

/// <summary>
/// The site an automatic install talks to. Requests go to this machine on <paramref name="Port"/> with
/// <paramref name="Alias"/> as the host name - the IIS binding routes them - so the install doesn't depend on the host
/// name resolving.
/// </summary>
/// <param name="Directory">The site's folder: web.config, bin, Install.</param>
/// <param name="Alias">The <c>host[:port]</c> the site answers on - it becomes the portal alias, e.g. <c>mysite.dnndev.me</c>.</param>
/// <param name="Port">The port the site's binding listens on.</param>
public sealed record DnnSiteAddress(string Directory, string Alias, int Port)
{
    /// <summary>The site's address for people: <c>http://mysite.dnndev.me/</c>.</summary>
    public string Url => $"http://{Alias}/";

    /// <summary>The portal alias for a host name and port: the port only when it isn't 80.</summary>
    public static string AliasFor(string hostName, int port) => port == 80 ? hostName : $"{hostName}:{port}";
}

/// <summary>
/// The rules a DNN host account and website must meet. DNN's unattended install doesn't check them: a password that is
/// too short leaves a site without a host account and without pages, and the installer has deleted itself by then.
/// </summary>
public static class DnnAccountRules
{
    /// <summary>The <c>minRequiredPasswordLength</c> of DNN's shipped membership provider.</summary>
    public const int MinPasswordLength = 7;

    /// <summary>The longest password ASP.NET's membership provider accepts.</summary>
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// DNN's extension packages have deep folders: from about 100 characters on, some of them fail to install (they hit
    /// Windows' 260-character path limit) while DNN still reports a complete installation.
    /// </summary>
    public const int MaxSitePathLength = 100;

    /// <summary>The cultures DNN's installer offers. Anything but en-US has DNN download its language pack during the install.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en-US", "de-DE", "es-ES", "fr-FR", "it-IT", "nl-NL"];

    /// <summary>The site templates DNN's install package ships (Portals\_default\*.template).</summary>
    public static readonly IReadOnlyList<string> Templates = ["Default Website", "Blank Website"];

    public static string? UserNameProblem(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return "Enter the host account's user name.";
        if (userName.Length > 100) return "The user name can have at most 100 characters.";
        return userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@')
            ? null
            : "The user name can only have letters, digits and . _ - @.";
    }

    public static string? PasswordProblem(string password) =>
        password.Length < MinPasswordLength ? $"The password needs at least {MinPasswordLength} characters."
        : password.Length > MaxPasswordLength ? $"The password can have at most {MaxPasswordLength} characters."
        // ASP.NET's request validation refuses these in DNN's login form - such a password could never sign in.
        : password.Contains('<') || password.Contains("&#", StringComparison.Ordinal) ? "The password can't contain < or &# - DNN's login form refuses them."
        : null;

    /// <summary>A strong random password DNN's login form accepts: letters, digits and a few symbols, no &lt; or &amp;#.</summary>
    public static string GeneratePassword(int length = 16)
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!%*-_=+.";
        var all = letters + digits + symbols;
        var chars = new List<char>
        {
            letters[System.Security.Cryptography.RandomNumberGenerator.GetInt32(24)],
            letters[24 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(letters.Length - 24)],
            digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[System.Security.Cryptography.RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (chars.Count < length) chars.Add(all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    public static string? EmailProblem(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('@', at + 1) < 0 && !email.Any(char.IsWhiteSpace) &&
               email[(at + 1)..].Contains('.')
            ? null
            : "Enter an e-mail address, e.g. host@example.com.";
    }

    public static string? WebsiteNameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter the website's name."
        : name.Length > 128 ? "The website's name can have at most 128 characters."
        : null;

    /// <summary>Everything wrong with <paramref name="account"/>, empty when DNN can install it.</summary>
    public static IReadOnlyList<string> Problems(DnnAccount account) =>
        new[]
        {
            UserNameProblem(account.UserName),
            PasswordProblem(account.Password),
            EmailProblem(account.Email),
            WebsiteNameProblem(account.WebsiteName),
            Languages.Contains(account.Language) ? null : $"DNN doesn't install in '{account.Language}'.",
            Templates.Contains(account.Template) ? null : $"DNN has no site template '{account.Template}'."
        }.OfType<string>().ToList();
}

/// <summary>
/// DNN's own unattended install (<c>Install/Install.aspx?mode=install</c>) and what belongs with it - the things DNN's
/// installation wizard would otherwise have the user do in the browser.
/// </summary>
public interface IDnnInstaller
{
    /// <summary>
    /// Whether the freshly extracted DNN in <paramref name="siteDirectory"/> can be installed unattended: its installer
    /// is there, it isn't installed yet, and its folder isn't too deep. Checked before anything else is set up.
    /// </summary>
    Result CheckPackage(string siteDirectory);

    /// <summary>
    /// For a LocalDB file database: DNN's install package ships <c>App_Data\Database.mdf</c> at SQL Server 2008's
    /// compatibility level, which DNN 10's modules can't upgrade in - it is raised to the server's level. Runs before the
    /// site first opens the database.
    /// </summary>
    Task<Result> PrepareLocalDbFileAsync(string siteDirectory, DatabaseConnection connection, CancellationToken ct);

    /// <summary>
    /// Installs DNN: writes the install template (host account, website, alias) right before asking the site to install
    /// itself, follows DNN's progress into <paramref name="reporter"/> and removes the template again whatever happens.
    /// Ok only when DNN reports a complete install without errors.
    /// </summary>
    Task<Result> InstallAsync(DnnSiteAddress site, DnnAccount account, DatabaseConnection database, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// After a successful install: checks the database holds the installed site, and leaves it as DNN's wizard does - the
    /// host signs in without being made to change the password. DNN caches users: restart the site afterwards. A LocalDB
    /// file database can only be opened while the site is stopped.
    /// </summary>
    Task<Result> CompleteAsync(DnnSiteAddress site, DnnAccount account, DatabaseConnection database, CancellationToken ct);

    /// <summary>
    /// The first visit after the install - DNN finishes upgrading its modules then - and what it shows: the home page,
    /// not the installer, and no errors in DNN's logs since <paramref name="installStartedUtc"/>.
    /// </summary>
    Task<Result> WarmUpAsync(DnnSiteAddress site, DateTime installStartedUtc, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Removes what still holds a password in plain text: the install template and - after an install - the copies of
    /// web.config DNN made while installing (they have the connection string).
    /// </summary>
    void CleanUp(string siteDirectory, bool installed);

    /// <summary>
    /// Sets the password of host account <paramref name="userName"/> the way DNN's membership provider stores it (only for
    /// a site that keeps hashed passwords). DNN caches users: restart the site afterwards. A LocalDB file database can only
    /// be opened while the site is stopped.
    /// </summary>
    Task<Result> ChangeHostPasswordAsync(string siteDirectory, DatabaseConnection database, string userName, string newPassword, CancellationToken ct);
}
