using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Single encrypted store for FTP and SQL connection profiles, persisted next to the application
/// as <c>connections.json</c> and keyed by project name:
/// <code>{ "metro_test": { "Ftp": { ... }, "Sql": { ... } } }</code>
/// Each project holds at most one FTP and one SQL connection. Implements both profile-store
/// interfaces and is registered as a single instance so the two kinds share one file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectionProfileStore : IFtpProfileStore, ISqlProfileStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ConnectionProfileStore()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "connections.json");
    }

    private sealed class ProjectConnections
    {
        public FtpProfile? Ftp { get; set; }
        public SqlProfile? Sql { get; set; }
    }

    // ─── FTP ────────────────────────────────────────────────────────────────
    FtpProfile? IFtpProfileStore.Get(string project)
    {
        lock (_lock) return Find(Load(), project)?.Ftp;
    }

    void IFtpProfileStore.Save(string project, FtpProfile profile)
    {
        lock (_lock)
        {
            var map = Load();
            Entry(map, project).Ftp = profile;
            Write(map);
        }
    }

    IReadOnlyList<string> IFtpProfileStore.ListProjects()
    {
        lock (_lock)
            return Load().Where(kv => kv.Value.Ftp is not null)
                         .Select(kv => kv.Key)
                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                         .ToList();
    }

    // ─── SQL ────────────────────────────────────────────────────────────────
    SqlProfile? ISqlProfileStore.Get(string project)
    {
        lock (_lock) return Find(Load(), project)?.Sql;
    }

    void ISqlProfileStore.Save(string project, SqlProfile profile)
    {
        lock (_lock)
        {
            var map = Load();
            Entry(map, project).Sql = profile;
            Write(map);
        }
    }

    IReadOnlyList<string> ISqlProfileStore.ListProjects()
    {
        lock (_lock)
            return Load().Where(kv => kv.Value.Sql is not null)
                         .Select(kv => kv.Key)
                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                         .ToList();
    }

    // ─── Shared DPAPI helpers (satisfy both interfaces) ──────────────────────
    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var enc = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(encrypted);
            var dec = ProtectedData.Unprotect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ProjectConnections? Find(Dictionary<string, ProjectConnections> map, string project)
        => map.TryGetValue(project, out var e) ? e : null;

    private static ProjectConnections Entry(Dictionary<string, ProjectConnections> map, string project)
    {
        if (!map.TryGetValue(project, out var e))
        {
            e = new ProjectConnections();
            map[project] = e;
        }
        return e;
    }

    private Dictionary<string, ProjectConnections> New()
        => new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ProjectConnections> Load()
    {
        if (!File.Exists(_path)) return New();
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ProjectConnections>>(File.ReadAllText(_path));
            if (parsed is null) return New();
            // Re-wrap with a case-insensitive comparer so project lookups are forgiving.
            return new Dictionary<string, ProjectConnections>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // An unreadable file starts an empty store, and the next Save would write over it -
            // silently destroying every saved connection. Set the damaged file aside first so the
            // passwords in it can still be recovered by hand.
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
            return New();
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private void Write(Dictionary<string, ProjectConnections> map)
    {
        var json = JsonSerializer.Serialize(map, WriteOptions);

        // Write-then-rename rather than writing in place: this file holds every saved credential,
        // and a crash (or a full disk) partway through an in-place write would truncate the lot.
        // The rename is atomic on NTFS, so readers see either the old file or the complete new one.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }
}
