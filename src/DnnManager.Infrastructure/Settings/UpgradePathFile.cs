using System.Text.Json;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// DNN's upgrade path as a file (Settings → DNN releases → Edit upgrade path → Export / Import): the whole path, each
/// version with what the step to it needs and its note - to keep, or to use on another PC. A file is data only: every
/// value goes through the rules the settings do, and an import only fills the dialog - Save keeps it.
/// </summary>
public static class UpgradePathFile
{
    public const string Kind = "dnnmanager.upgradePath";
    public const string Filter = "DNN upgrade path (*.json)|*.json";
    /// <summary>A path is a few kilobytes; more isn't one.</summary>
    public const long MaxBytes = 1024 * 1024;
    public const int MaxVersions = 1000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        // Made to be read and changed by hand too.
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    private sealed class FileVersion
    {
        public string? Version { get; set; }
        public int NetFrameworkRelease { get; set; }
        public int SqlServerMajor { get; set; }
        public string? Note { get; set; }
    }

    private sealed class FileModel
    {
        public string? Kind { get; set; }
        /// <summary>
        /// The newest version the DNN Manager that wrote it lists: one listed later isn't missing from the file, it
        /// wasn't known yet - an import keeps it.
        /// </summary>
        public string? KnownUpTo { get; set; }
        public List<FileVersion>? Path { get; set; }
    }

    /// <summary><paramref name="path"/> as the file's text, oldest first.</summary>
    public static string ToJson(IEnumerable<DnnPathVersion> path) => JsonSerializer.Serialize(new FileModel
    {
        Kind = Kind,
        KnownUpTo = DnnUpgradePath.Versions[^1].ToString(),
        Path = path.OrderBy(v => v.Version).Select(v => new FileVersion
        {
            Version = v.Version.ToString(), NetFrameworkRelease = v.NetFrameworkRelease, SqlServerMajor = v.SqlServerMajor, Note = v.Note
        }).ToList()
    }, Options);

    public static Result Write(string file, IEnumerable<DnnPathVersion> path)
    {
        try
        {
            File.WriteAllText(file, ToJson(path));
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ex.Message);
        }
    }

    public static Result<IReadOnlyList<DnnPathVersion>> Read(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxBytes) return Result<IReadOnlyList<DnnPathVersion>>.Fail("it is too big to be an upgrade path.");
            return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<DnnPathVersion>>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The path in <paramref name="json"/>, oldest first - with the versions this DNN Manager lists that the one which
    /// wrote it didn't know yet - or why it can't be used.
    /// </summary>
    public static Result<IReadOnlyList<DnnPathVersion>> Parse(string json)
    {
        static Result<IReadOnlyList<DnnPathVersion>> Fail(string why) => Result<IReadOnlyList<DnnPathVersion>>.Fail(why);
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(json, Options);
        }
        catch (JsonException ex)
        {
            return Fail($"it isn't an upgrade path exported by DNN Manager ({ex.Message}).");
        }
        if (model is null || !string.Equals(model.Kind, Kind, StringComparison.Ordinal) || model.Path is null)
            return Fail("it isn't an upgrade path exported by DNN Manager.");
        if (model.Path.Count == 0) return Fail("it has no versions.");
        if (model.Path.Count > MaxVersions) return Fail($"it has more than {MaxVersions} versions.");

        var stops = model.Path.Select(v => new DnnUpgradeStopSetting
        {
            Version = v?.Version ?? "", NetFrameworkRelease = v?.NetFrameworkRelease ?? 0, SqlServerMajor = v?.SqlServerMajor ?? 0, Note = v?.Note ?? ""
        }).ToList();
        if (SettingRules.UpgradeStopsProblem(stops) is { } problem) return Fail(problem);

        var path = stops.Select(s => new DnnPathVersion(SettingRules.UpgradeStopVersion(s.Version)!, s.NetFrameworkRelease, s.SqlServerMajor, s.Note.Trim())).ToList();
        if (SettingRules.UpgradeStopVersion(model.KnownUpTo) is { } knownUpTo)
            path.AddRange(DnnUpgradePath.Versions.Where(v => v > knownUpTo && path.All(p => p.Version != v)).Select(DnnUpgradePath.Listed));
        return Result<IReadOnlyList<DnnPathVersion>>.Ok(path.OrderBy(v => v.Version).ToList());
    }
}
