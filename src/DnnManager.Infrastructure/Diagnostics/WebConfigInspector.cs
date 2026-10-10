using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Diagnostics;

/// <summary>A binding redirect in web.config's runtime section: <paramref name="Name"/> from <paramref name="OldVersion"/> to <paramref name="NewVersion"/>.</summary>
public sealed record BindingRedirect(string Name, string OldVersion, string NewVersion);

/// <summary>A codeBase in web.config's runtime section: <paramref name="Name"/> <paramref name="Version"/> loads from <paramref name="Href"/> - a file outside the probing path, often bin\&lt;module&gt;.</summary>
public sealed record CodeBase(string Name, string Version, string Href);

/// <summary>
/// What a site's web.config says that a developer looks for - never a password or a key, only whether one is set.
/// A value that is null wasn't in the file: ASP.NET, IIS or DNN then use their default (see the *Default constants).
/// </summary>
public sealed record WebConfigInspection
{
    /// <summary>ASP.NET's defaults, for a value web.config doesn't set.</summary>
    public const int DefaultMaxRequestLengthKb = 4096, DefaultExecutionTimeoutSeconds = 110;
    public const long DefaultMaxAllowedContentLength = 30_000_000;

    public required string Path { get; init; }
    /// <summary>Why it couldn't be read; null when it was.</summary>
    public string? Problem { get; init; }
    public DateTime? Modified { get; init; }

    // system.web
    public bool? Debug { get; init; }
    public string? CompilationTargetFramework { get; init; }
    public string? RuntimeTargetFramework { get; init; }
    public string? CustomErrors { get; init; }
    public int? MaxRequestLengthKb { get; init; }
    public int? ExecutionTimeoutSeconds { get; init; }
    public string? RequestValidationMode { get; init; }
    public string? SessionState { get; init; }
    public string? AuthenticationMode { get; init; }
    /// <summary>null: no machineKey element (ASP.NET generates keys per machine); else how its keys are set.</summary>
    public string? MachineKey { get; init; }

    // system.webServer
    public long? MaxAllowedContentLength { get; init; }
    public long? MaxUrl { get; init; }
    public long? MaxQueryString { get; init; }
    public bool? StaticCompression { get; init; }
    public bool? DynamicCompression { get; init; }
    public string? ClientCache { get; init; }
    public string? HttpErrors { get; init; }
    public bool? RunAllManagedModules { get; init; }
    public int RewriteRules { get; init; }

    // dotnetnuke
    public bool HasDnnSection { get; init; }
    public string? FriendlyUrlProvider { get; init; }
    public string? FriendlyUrlFormat { get; init; }
    public string? CachingProvider { get; init; }
    public string? LoggingProvider { get; init; }
    public string? DataProvider { get; init; }
    public string? ObjectQualifier { get; init; }
    public string? DatabaseOwner { get; init; }

    // appSettings
    public string? InstallVersion { get; init; }
    public string? InstallationDate { get; init; }
    public string? AutoUpgrade { get; init; }

    // runtime
    public IReadOnlyList<BindingRedirect> BindingRedirects { get; init; } = [];
    public IReadOnlyList<CodeBase> CodeBases { get; init; } = [];
    public string? ProbingPath { get; init; }

    // The SiteSqlServer connection's options - never its password.
    public string? SqlServer { get; init; }
    public string? SqlDatabase { get; init; }
    public bool? SqlIntegratedSecurity { get; init; }
    public string? SqlUser { get; init; }
    public string? SqlEncrypt { get; init; }
    public bool? SqlTrustServerCertificate { get; init; }
    public int? SqlConnectTimeout { get; init; }
    public bool? SqlMultipleActiveResultSets { get; init; }
    public string? SqlApplicationName { get; init; }
    public string? SqlAttachDbFile { get; init; }
    /// <summary>Sections kept in a file of their own (configSource) - not read here.</summary>
    public IReadOnlyList<string> ExternalSections { get; init; } = [];
}

/// <summary>Reads a site's web.config for its Details page. Never throws: what can't be read is in <see cref="WebConfigInspection.Problem"/>.</summary>
public static class WebConfigInspector
{
    private static readonly XNamespace Asm = "urn:schemas-microsoft-com:asm.v1";

    public static WebConfigInspection Inspect(string directory)
    {
        var path = System.IO.Path.Combine(directory, "web.config");
        if (!File.Exists(path)) return new WebConfigInspection { Path = path, Problem = "not found" };
        XElement root;
        try
        {
            // The site's web.config, which its app pool can change: read without a DTD or a resolver.
            root = WebConfigs.SiteXml.Load(path).Root ?? throw new System.Xml.XmlException("the file is empty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new WebConfigInspection { Path = path, Problem = $"can't be read: {ex.Message}" };
        }

        var web = root.Element("system.web");
        var server = root.Element("system.webServer");
        var dnn = root.Element("dotnetnuke");
        var compilation = web?.Element("compilation");
        var runtime = web?.Element("httpRuntime");
        var limits = server?.Element("security")?.Element("requestFiltering")?.Element("requestLimits");
        var compression = server?.Element("urlCompression");
        var cache = server?.Element("staticContent")?.Element("clientCache");
        var external = root.Descendants().Where(e => e.Attribute("configSource") is not null).Select(e => e.Name.LocalName).ToList();

        var (friendlyProvider, friendlyFormat) = Provider(dnn?.Element("friendlyUrl"), "urlFormat");
        var (dataProvider, _) = Provider(dnn?.Element("data"), null);
        var dataElement = DefaultProviderElement(dnn?.Element("data"));

        // runtime can have more than one assemblyBinding, and a dependentAssembly more than one bindingRedirect and codeBase
        // (one per version range - Imageflow's 2.2.0.0 from bin\Imageflow next to 2.1.1.0 in bin).
        var bindings = root.Element("runtime")?.Elements(Asm + "assemblyBinding").ToList() ?? [];
        var dependents = bindings.SelectMany(b => b.Elements(Asm + "dependentAssembly"))
            .Select(d => (Name: (string?)d.Element(Asm + "assemblyIdentity")?.Attribute("name") ?? "", Element: d))
            .Where(d => d.Name.Length > 0)
            .ToList();
        var redirects = dependents
            .SelectMany(d => d.Element.Elements(Asm + "bindingRedirect").Select(r =>
                new BindingRedirect(d.Name, (string?)r.Attribute("oldVersion") ?? "", (string?)r.Attribute("newVersion") ?? "")))
            .ToList();
        var codeBases = dependents
            .SelectMany(d => d.Element.Elements(Asm + "codeBase").Select(c =>
                new CodeBase(d.Name, (string?)c.Attribute("version") ?? "", (string?)c.Attribute("href") ?? "")))
            .Where(c => c.Href.Length > 0)
            .ToList();
        var probing = bindings.Select(b => (string?)b.Element(Asm + "probing")?.Attribute("privatePath")).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        var inspection = new WebConfigInspection
        {
            Path = path,
            Modified = File.GetLastWriteTime(path),
            Debug = Bool(compilation, "debug"),
            CompilationTargetFramework = (string?)compilation?.Attribute("targetFramework"),
            RuntimeTargetFramework = (string?)runtime?.Attribute("targetFramework"),
            CustomErrors = (string?)web?.Element("customErrors")?.Attribute("mode"),
            MaxRequestLengthKb = Int(runtime, "maxRequestLength"),
            ExecutionTimeoutSeconds = Int(runtime, "executionTimeout"),
            RequestValidationMode = (string?)runtime?.Attribute("requestValidationMode"),
            SessionState = (string?)web?.Element("sessionState")?.Attribute("mode"),
            AuthenticationMode = (string?)web?.Element("authentication")?.Attribute("mode"),
            MachineKey = MachineKey(web?.Element("machineKey")),
            MaxAllowedContentLength = Long(limits, "maxAllowedContentLength"),
            MaxUrl = Long(limits, "maxUrl"),
            MaxQueryString = Long(limits, "maxQueryString"),
            StaticCompression = Bool(compression, "doStaticCompression"),
            DynamicCompression = Bool(compression, "doDynamicCompression"),
            ClientCache = cache is null ? null
                : $"{(string?)cache.Attribute("cacheControlMode") ?? "NoControl"}" +
                  ((string?)cache.Attribute("cacheControlMaxAge") is { } age ? $", max-age {age}" : ""),
            HttpErrors = (string?)server?.Element("httpErrors")?.Attribute("errorMode"),
            RunAllManagedModules = Bool(server?.Element("modules"), "runAllManagedModulesForAllRequests"),
            RewriteRules = server?.Element("rewrite")?.Element("rules")?.Elements("rule").Count() ?? 0,
            HasDnnSection = dnn is not null,
            FriendlyUrlProvider = friendlyProvider,
            FriendlyUrlFormat = friendlyFormat,
            CachingProvider = Provider(dnn?.Element("caching"), null).Name,
            LoggingProvider = Provider(dnn?.Element("logging"), null).Name,
            DataProvider = dataProvider,
            ObjectQualifier = (string?)dataElement?.Attribute("objectQualifier"),
            DatabaseOwner = (string?)dataElement?.Attribute("databaseOwner"),
            InstallVersion = AppSetting(root, "InstallVersion"),
            InstallationDate = AppSetting(root, "InstallationDate"),
            AutoUpgrade = AppSetting(root, "AutoUpgrade"),
            BindingRedirects = redirects,
            CodeBases = codeBases,
            ProbingPath = probing.Count == 0 ? null : string.Join(";", probing),
            ExternalSections = external
        };
        return WithConnection(inspection, root);
    }

    /// <summary>The SiteSqlServer connection's options - parsed, so its password never leaves here.</summary>
    private static WebConfigInspection WithConnection(WebConfigInspection inspection, XElement root)
    {
        var raw = (string?)root.Element("connectionStrings")?.Elements("add")
            .FirstOrDefault(a => (string?)a.Attribute("name") == "SiteSqlServer")?.Attribute("connectionString");
        if (string.IsNullOrWhiteSpace(raw)) return inspection;
        try
        {
            var b = new SqlConnectionStringBuilder(raw);
            return inspection with
            {
                SqlServer = b.DataSource,
                SqlDatabase = b.InitialCatalog,
                SqlIntegratedSecurity = b.IntegratedSecurity,
                SqlUser = b.IntegratedSecurity ? null : b.UserID,
                SqlEncrypt = Has(b, "Encrypt") ? b.Encrypt.ToString() : null,
                SqlTrustServerCertificate = Has(b, "TrustServerCertificate") ? b.TrustServerCertificate : null,
                SqlConnectTimeout = Has(b, "Connect Timeout") ? b.ConnectTimeout : null,
                SqlMultipleActiveResultSets = Has(b, "MultipleActiveResultSets") ? b.MultipleActiveResultSets : null,
                SqlApplicationName = Has(b, "Application Name") ? b.ApplicationName : null,
                SqlAttachDbFile = string.IsNullOrEmpty(b.AttachDBFilename) ? null : b.AttachDBFilename
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return inspection;
        }

        static bool Has(SqlConnectionStringBuilder b, string key) => b.ShouldSerialize(key);
    }

    /// <summary>A DNN provider section's default provider - and one attribute of its entry.</summary>
    private static (string? Name, string? Attribute) Provider(XElement? section, string? attribute)
    {
        if (section is null) return (null, null);
        var name = (string?)section.Attribute("defaultProvider");
        var entry = DefaultProviderElement(section);
        return (name, attribute is null ? null : (string?)entry?.Attribute(attribute));
    }

    private static XElement? DefaultProviderElement(XElement? section)
    {
        var name = (string?)section?.Attribute("defaultProvider");
        return name is null ? null
            : section!.Element("providers")?.Elements("add").FirstOrDefault(a => (string?)a.Attribute("name") == name);
    }

    /// <summary>How machineKey's keys are set - "auto-generated", "fixed keys" - never the keys.</summary>
    private static string? MachineKey(XElement? key)
    {
        if (key is null) return null;
        var validation = (string?)key.Attribute("validationKey") ?? "AutoGenerate";
        var decryption = (string?)key.Attribute("decryptionKey") ?? "AutoGenerate";
        var auto = validation.StartsWith("AutoGenerate", StringComparison.OrdinalIgnoreCase) &&
                   decryption.StartsWith("AutoGenerate", StringComparison.OrdinalIgnoreCase);
        var algorithms = $"{(string?)key.Attribute("validation") ?? "default"} / {(string?)key.Attribute("decryption") ?? "Auto"}";
        return $"{(auto ? "auto-generated keys" : "fixed keys")} ({algorithms})";
    }

    private static string? AppSetting(XElement root, string key) =>
        (string?)root.Element("appSettings")?.Elements("add").FirstOrDefault(a => (string?)a.Attribute("key") == key)?.Attribute("value");

    private static bool? Bool(XElement? element, string name) =>
        (string?)element?.Attribute(name) is { } value && bool.TryParse(value, out var b) ? b : null;

    private static int? Int(XElement? element, string name) =>
        (string?)element?.Attribute(name) is { } value && int.TryParse(value, out var i) ? i : null;

    private static long? Long(XElement? element, string name) =>
        (string?)element?.Attribute(name) is { } value && long.TryParse(value, out var l) ? l : null;
}
