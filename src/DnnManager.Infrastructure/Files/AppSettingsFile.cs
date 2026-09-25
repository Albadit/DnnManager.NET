using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Writes the user-editable settings back into the <c>appsettings.json</c> next to the app. Only the
/// keys the settings screen edits are replaced - everything else in the file (logging, the IIS
/// feature list, unknown keys) is kept as it is.
/// </summary>
public static class AppSettingsFile
{
    public static string FullPath => BundledFiles.PathOf(BundledFiles.AppSettings);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep paths and URLs readable ("C:\\DNN", not "C:\u005CDNN").
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Save(AppOptions options)
    {
        var root = Read();
        var section = Child(root, AppOptions.SectionName);
        section["BaseDirectory"] = options.BaseDirectory;
        section["SitePort"] = options.SitePort;
        section["HostnameSuffix"] = options.HostnameSuffix;
        section["GitHubReleaseApis"] = new JsonArray(options.GitHubReleaseApis.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());

        var docker = Child(section, "Docker");
        docker["ContainerName"] = options.Docker.ContainerName;
        docker["ContainerIp"] = options.Docker.ContainerIp;
        docker["VolumeName"] = options.Docker.VolumeName;
        docker["SaPassword"] = options.Docker.SaPassword;
        docker["DefaultPort"] = options.Docker.DefaultPort;
        docker["Collation"] = options.Docker.Collation;
        docker["MssqlPid"] = options.Docker.MssqlPid;
        docker["DefaultDbNameSuffix"] = options.Docker.DefaultDbNameSuffix;

        Write(root);
    }

    /// <summary>Saves only <c>DnnManager:Theme</c> ("Light" / "Dark" / "System").</summary>
    public static void SaveTheme(string theme)
    {
        var root = Read();
        Child(root, AppOptions.SectionName)["Theme"] = theme;
        Write(root);
    }

    private static JsonObject Read()
    {
        var path = FullPath;
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? new JsonObject()
            : new JsonObject();
    }

    private static void Write(JsonObject root)
    {
        // Write beside it and move into place, so a crash never leaves a half-written config behind.
        var path = FullPath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(tmp, path, overwrite: true);
    }

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
