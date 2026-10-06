using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Iis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static DnnManager.Infrastructure.Broker.BrokerProtocol;

namespace DnnManager.Infrastructure.Broker;

/// <summary>
/// IIS for a DNN Manager without Administrator rights: what the broker knows (starting, stopping and restarting a site,
/// and every site's state - what the Projects table needs) goes to the DNN Manager Broker service; everything else still
/// runs here, as <paramref name="local"/> does, and fails where it needs the rights. The experiment moves operations
/// over one at a time (.docs/privileged-broker.md).
/// </summary>
public sealed class BrokeredIisManager(IisManager local, BrokerClient broker, ILogger<BrokeredIisManager> log) : IIisManager
{
    /// <summary>What a site operation may take: stopping waits up to IIS's shutdown time limit for the worker process.</summary>
    private static readonly TimeSpan SiteTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly IisManager _local = local;
    private readonly BrokerClient _broker = broker;
    private readonly ILogger<BrokeredIisManager> _log = log;

    // ─── Through the broker ───────────────────────────────────────────────

    public Result StartSite(string siteName) => Run(BrokerRequest.For(Operations.StartSite, siteName));

    public Result StopSite(string siteName) => Run(BrokerRequest.For(Operations.StopSite, siteName));

    public Result StopSiteAndWait(string siteName, TimeSpan timeout) =>
        Run(BrokerRequest.For(Operations.StopSiteAndWait, siteName, (int)Math.Ceiling(timeout.TotalSeconds)));

    public Result RestartSite(string siteName) => Run(BrokerRequest.For(Operations.RestartSite, siteName));

    public IReadOnlyDictionary<string, string> GetSiteStates()
    {
        var states = Read<Dictionary<string, string>>(Operations.SiteStates);
        return states is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(states, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IisSiteRuntime>? GetSiteRuntimes()
    {
        // Null - IIS couldn't be read - lets the Projects table keep what it knew.
        var runtimes = Read<Dictionary<string, IisSiteRuntime>>(Operations.SiteRuntimes);
        return runtimes is null ? null : new Dictionary<string, IisSiteRuntime>(runtimes, StringComparer.OrdinalIgnoreCase);
    }

    private Result Run(BrokerRequest request)
    {
        try
        {
            var response = _broker.Send(request, SiteTimeout);
            return response.Success ? Result.Ok() : Result.Fail(response.Error ?? $"{request.Operation} failed.");
        }
        catch (BrokerUnavailableException ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    private T? Read<T>(string operation) where T : class
    {
        try
        {
            var response = _broker.Send(BrokerRequest.For(operation), ReadTimeout);
            if (response.Success) return response.DataAs<T>();
            _log.LogWarning("The DNN Manager Broker couldn't answer {Operation}: {Error}", operation, response.Error);
        }
        catch (BrokerUnavailableException ex)
        {
            _log.LogWarning("The DNN Manager Broker couldn't be asked for {Operation}: {Error}", operation, ex.Message);
        }
        return null;
    }

    // ─── Still here (needs Administrator rights for anything that changes IIS) ───

    public Result CreateSite(string siteName, string physicalPath, string hostname, int port) => _local.CreateSite(siteName, physicalPath, hostname, port);
    public Result RemoveSite(string siteName) => _local.RemoveSite(siteName);
    public Result RecycleAppPool(string siteName) => _local.RecycleAppPool(siteName);
    public string? GetLogDirectory(string siteName) => _local.GetLogDirectory(siteName);
    public string AppPoolIdentity(string siteName) => _local.AppPoolIdentity(siteName);
    public Result EnableUserProfile(string siteName) => _local.EnableUserProfile(siteName);
    public bool IsAvailable() => _local.IsAvailable();
    public IisServerState GetServerState() => _local.GetServerState();
    public Task<Result> ControlServerAsync(IisServerAction action, CancellationToken ct) => _local.ControlServerAsync(action, ct);
    public IReadOnlyDictionary<string, SiteTraffic> GetSiteTraffic() => _local.GetSiteTraffic();
    public IReadOnlyDictionary<string, long> GetRequestsServed() => _local.GetRequestsServed();
    public IisSiteDetails? GetSiteDetails(string siteName) => _local.GetSiteDetails(siteName);
    public Result ReplaceHttpBindings(string siteName, IReadOnlyList<(string Host, int Port)> bindings) => _local.ReplaceHttpBindings(siteName, bindings);
    public Result SetPoolSettings(string siteName, IisPoolSettings settings) => _local.SetPoolSettings(siteName, settings);
    public Result RenameSite(string siteName, string newName, string physicalPath) => _local.RenameSite(siteName, newName, physicalPath);
    public Result GrantPermissions(string path, IEnumerable<string> identities) => _local.GrantPermissions(path, identities);
    public Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct) => _local.RemoveAppPoolProfileAsync(poolName, ct);
}

public static class BrokerServiceCollectionExtensions
{
    /// <summary>IIS through the DNN Manager Broker service - for a DNN Manager that runs without Administrator rights.</summary>
    public static IServiceCollection AddBrokeredIis(this IServiceCollection services)
    {
        services.AddSingleton<IisManager>();
        services.AddSingleton<BrokerClient>();
        services.Replace(ServiceDescriptor.Singleton<IIisManager, BrokeredIisManager>());
        return services;
    }
}
