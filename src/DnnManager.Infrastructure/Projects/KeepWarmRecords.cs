using System.Text.Json;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Projects;

/// <summary>
/// <see cref="KeepWarmRecord"/>s as JSON files in <c>Documents\DnnManager\projects\keep-warm</c>, one per site. A record
/// that can't be read counts as none: the site isn't kept warm until it is switched on again.
/// </summary>
public sealed class KeepWarmRecords(AppDataPaths paths, ILogger<KeepWarmRecords> log) : IKeepWarmRecords
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDataPaths _paths = paths;
    private readonly ILogger<KeepWarmRecords> _log = log;

    public KeepWarmRecord? Find(string site)
    {
        var file = FileOf(site);
        // Another site whose name only differs in characters a file name can't have isn't this one.
        return File.Exists(file) && Read(file) is { } record && record.Site.Equals(site, StringComparison.OrdinalIgnoreCase) ? record : null;
    }

    public IReadOnlyList<KeepWarmRecord> List()
    {
        try
        {
            if (!Directory.Exists(_paths.KeepWarmDirectory)) return [];
            return Directory.EnumerateFiles(_paths.KeepWarmDirectory, "*.json").Select(Read).OfType<KeepWarmRecord>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list the keep-warm records");
            return [];
        }
    }

    public void Save(KeepWarmRecord record)
    {
        if (record.IsEmpty)
        {
            Remove(record.Site);
            return;
        }
        try
        {
            Directory.CreateDirectory(_paths.KeepWarmDirectory);
            File.WriteAllText(FileOf(record.Site), JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the keep-warm record of {Site}", record.Site);
        }
    }

    public void Remove(string site)
    {
        try { File.Delete(FileOf(site)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not remove the keep-warm record of {Site}", site); }
    }

    private KeepWarmRecord? Read(string file)
    {
        try
        {
            // The site's name is in the file; the file's own name may have had characters replaced.
            return JsonSerializer.Deserialize<KeepWarmRecord>(File.ReadAllText(file), Json) is { Site.Length: > 0 } record ? record : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not read the keep-warm record {File}", file);
            return null;
        }
    }

    private string FileOf(string site)
    {
        var name = string.Concat(site.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_paths.KeepWarmDirectory, name + ".json");
    }
}
