using System.Text.Json.Nodes;
using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Upgrades <c>settings.json</c> from <see cref="FromVersion"/> to the next version, on the raw JSON - so it
/// works on files whose layout the current <see cref="UserSettings"/> no longer matches.
/// </summary>
public interface ISettingsMigration
{
    int FromVersion { get; }
    void Apply(JsonObject root);
}

/// <summary>
/// Every migration, oldest first. To change the settings layout: raise <see cref="UserSettings.CurrentVersion"/>
/// and add a migration from the previous version here - the store backs the file up and runs the chain.
/// </summary>
public static class SettingsMigrations
{
    public static readonly IReadOnlyList<ISettingsMigration> All =
    [
        new V0ToV1()
    ];

    /// <summary>Runs the migrations that take <paramref name="root"/> from <paramref name="version"/> to the current one.</summary>
    public static void Apply(JsonObject root, int version)
    {
        for (var v = version; v < UserSettings.CurrentVersion; v++)
        {
            var migration = All.SingleOrDefault(m => m.FromVersion == v)
                ?? throw new InvalidOperationException($"No settings migration from version {v}.");
            migration.Apply(root);
            if (root.ContainsKey("version")) root["version"] = v + 1;
            else root.Insert(0, "version", v + 1); // first in the file, where people look for it
        }
    }

    /// <summary>
    /// Version 0 is the <c>appsettings.json</c> of DNN Manager 2.0 and earlier: everything in a
    /// <c>DnnManager</c> section, next to a <c>Logging</c> one. Version 1 groups the values by what they are
    /// about and drops the unused logging levels. A file without a <c>DnnManager</c> section only gets its version.
    /// </summary>
    private sealed class V0ToV1 : ISettingsMigration
    {
        public int FromVersion => 0;

        public void Apply(JsonObject root)
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

        private static JsonObject Section(JsonObject root, string key)
        {
            if (root[key] is JsonObject existing) return existing;
            var created = new JsonObject();
            root[key] = created;
            return created;
        }

        private static void Move(JsonObject from, string fromKey, JsonObject to, string toKey)
        {
            if (from[fromKey] is { } value) to[toKey] = value.DeepClone();
        }
    }
}
