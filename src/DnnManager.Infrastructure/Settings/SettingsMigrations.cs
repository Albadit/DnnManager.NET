using System.Text.Json.Nodes;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Upgrades <c>settings.json</c> from <see cref="FromVersion"/> to the next version, on the raw JSON - so it
/// works on files whose layout the current <see cref="UserSettings"/> no longer matches.
/// </summary>
public interface ISettingsMigration
{
    int FromVersion { get; }

    /// <param name="secrets">The Windows Credential Manager, for a migration that moves a secret; null to leave secrets alone.</param>
    void Apply(JsonObject root, ISecretStore? secrets);
}

/// <summary>
/// Every migration, oldest first. To change the settings layout: raise <see cref="UserSettings.CurrentVersion"/>
/// and add a migration from the previous version here - the store backs the file up and runs the chain.
/// </summary>
public static class SettingsMigrations
{
    public static readonly IReadOnlyList<ISettingsMigration> All =
    [
        new V0ToV1(),
        new V1ToV2(),
        new V2ToV3()
    ];

    /// <summary>Runs the migrations that take <paramref name="root"/> from <paramref name="version"/> to the current one.</summary>
    public static void Apply(JsonObject root, int version, ISecretStore? secrets)
    {
        for (var v = version; v < UserSettings.CurrentVersion; v++)
        {
            var migration = All.SingleOrDefault(m => m.FromVersion == v)
                ?? throw new InvalidOperationException($"No settings migration from version {v}.");
            migration.Apply(root, secrets);
            if (root.ContainsKey("version")) root["version"] = v + 1;
            else root.Insert(0, "version", v + 1); // first in the file, where people look for it
        }
    }

    /// <summary>
    /// Version 0 is the <c>appsettings.json</c> of DNN Manager 1.1 and earlier: everything in a
    /// <c>DnnManager</c> section, next to a <c>Logging</c> one. Version 1 groups the values by what they are
    /// about and drops the unused logging levels. A file without a <c>DnnManager</c> section only gets its version.
    /// </summary>
    private sealed class V0ToV1 : ISettingsMigration
    {
        public int FromVersion => 0;

        public void Apply(JsonObject root, ISecretStore? secrets)
        {
            root.Remove("Logging");
            if (root["DnnManager"] is not JsonObject old)
            {
                root.Remove("DnnManager");
                return;
            }
            root.Remove("DnnManager");

            var projects = Section(root, "projects");
            Move(old, "BaseDirectory", projects, "baseDirectory");
            Move(old, "HostnameSuffix", projects, "hostnameSuffix");
            Move(old, "SitePort", projects, "sitePort");
            Move(old, "GitHubReleaseApis", projects, "dnnReleaseSources");

            var sql = Section(root, "sqlServer");
            if (old["Docker"] is JsonObject docker)
            {
                Move(docker, "ContainerIp", sql, "host");
                Move(docker, "DefaultPort", sql, "port");
                Move(docker, "SaPassword", sql, "saPassword");
                Move(docker, "DefaultDbNameSuffix", sql, "databaseNameSuffix");
                Move(docker, "ContainerName", sql, "containerName");
                Move(docker, "VolumeName", sql, "volumeName");
                Move(docker, "MssqlPid", sql, "edition");
                Move(docker, "Collation", sql, "collation");
            }

            Move(old, "SsmsRememberPassword", Section(root, "ssms"), "rememberPassword");

            if (old["RequiredIisFeatures"] is JsonArray features)
            {
                var renamed = new JsonArray();
                foreach (var feature in features.OfType<JsonObject>())
                    renamed.Add(new JsonObject
                    {
                        ["name"] = feature["Name"]?.DeepClone(),
                        ["label"] = feature["Label"]?.DeepClone()
                    });
                Section(root, "iis")["requiredFeatures"] = renamed;
            }

            // "Light" / "Dark" / "System" become lower case.
            if (old["Theme"] is JsonValue theme && theme.TryGetValue<string>(out var name))
                Section(root, "appearance")["theme"] = name.ToLowerInvariant();
        }

        internal static JsonObject Section(JsonObject root, string key)
        {
            if (root[key] is JsonObject existing) return existing;
            var created = new JsonObject();
            root[key] = created;
            return created;
        }

        internal static void Move(JsonObject from, string fromKey, JsonObject to, string toKey)
        {
            if (from[fromKey] is { } value) to[toKey] = value.DeepClone();
        }
    }

    /// <summary>
    /// Version 2 puts the Docker container's values in a <c>docker</c> section of their own, apart from the SQL
    /// Server connection: <c>sqlServer.containerName</c>, <c>volumeName</c>, <c>edition</c> and <c>collation</c> move
    /// to <c>docker</c>. <c>sqlServer.databaseNameSuffix</c> is dropped - a new project's
    /// database is named like the project.
    /// </summary>
    private sealed class V1ToV2 : ISettingsMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root, ISecretStore? secrets)
        {
            if (root["sqlServer"] is not JsonObject sql) return;
            sql.Remove("databaseNameSuffix");
            var docker = V0ToV1.Section(root, "docker");
            foreach (var key in new[] { "containerName", "volumeName", "edition", "collation" })
            {
                V0ToV1.Move(sql, key, docker, key);
                sql.Remove(key);
            }
        }
    }

    /// <summary>
    /// Version 3 drops the database profiles: there is one database server, chosen in Settings → Database server.
    /// The profile new projects started with (<c>projects.defaultDatabaseProfile</c>) becomes it - its type, server,
    /// authentication and login go to <c>sqlServer</c>, and its password to the server's name in the Credential
    /// Manager. <c>projects.databaseProfiles</c> and <c>projects.defaultDatabaseProfile</c> are removed, with every
    /// profile's password. Without a chosen profile the server is the local container, as before. The Projects table's
    /// Site column, a default one from now on, is added at the front of <c>appearance.projectColumns</c>.
    /// </summary>
    private sealed class V2ToV3 : ISettingsMigration
    {
        public int FromVersion => 2;

        public void Apply(JsonObject root, ISecretStore? secrets)
        {
            var sql = V0ToV1.Section(root, "sqlServer");
            var projects = root["projects"] as JsonObject;
            var profiles = (projects?["databaseProfiles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var chosenId = Text(projects?["defaultDatabaseProfile"]);
            var chosen = profiles.FirstOrDefault(p => Text(p["id"]) is { Length: > 0 } id &&
                                                      id.Equals(chosenId, StringComparison.OrdinalIgnoreCase));

            sql["type"] = chosen is null ? SqlServerSettings.ContainerType : Text(chosen["type"]) ?? "sqlServer";
            if (chosen is not null)
            {
                V0ToV1.Move(chosen, "server", sql, "server");
                V0ToV1.Move(chosen, "authentication", sql, "authentication");
                V0ToV1.Move(chosen, "userName", sql, "userName");
            }

            if (secrets is not null)
            {
                if (chosen is not null && secrets.Read(SecretNames.LegacyDatabaseProfilePassword(Text(chosen["id"])!)) is { Length: > 0 } password)
                    secrets.Write(SecretNames.DatabaseServerPassword, password);
                foreach (var id in profiles.Select(p => Text(p["id"])).OfType<string>().Where(id => id.Length > 0))
                    secrets.Delete(SecretNames.LegacyDatabaseProfilePassword(id));
            }

            projects?.Remove("databaseProfiles");
            projects?.Remove("defaultDatabaseProfile");

            if (root["appearance"] is JsonObject appearance && appearance["projectColumns"] is JsonArray columns &&
                !columns.Any(c => Text(c) is { } key && key.Equals("url", StringComparison.OrdinalIgnoreCase)))
                columns.Insert(0, "url");
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
