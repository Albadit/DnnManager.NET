using System.Text.Json;
using System.Text.Json.Serialization;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Projects;

/// <summary>
/// <see cref="ProjectRecord"/>s as JSON files in <c>Documents\DnnManager\projects</c>, one per project. They hold no
/// secrets. A record that can't be read counts as no record - it is only what DNN Manager remembers, never the site.
/// </summary>
public sealed class ProjectRecords : IProjectRecords
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppDataPaths _paths;
    private readonly ILogger<ProjectRecords> _log;

    public ProjectRecords(AppDataPaths paths, ILogger<ProjectRecords> log)
    {
        _paths = paths;
        _log = log;
    }

    public ProjectRecord? Find(string site)
    {
        var file = FileOf(site);
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectRecord>(File.ReadAllText(file), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the record of {Site}", site);
            return null;
        }
    }

    public void Save(ProjectRecord record)
    {
        try
        {
            Directory.CreateDirectory(_paths.ProjectRecordsDirectory);
            File.WriteAllText(FileOf(record.Site), JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the record of {Site}", record.Site);
        }
    }

    public void Remove(string site)
    {
        try { File.Delete(FileOf(site)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not remove the record of {Site}", site); }
    }

    private string FileOf(string site)
    {
        var name = string.Concat(site.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_paths.ProjectRecordsDirectory, name + ".json");
    }
}