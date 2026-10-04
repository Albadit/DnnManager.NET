namespace DnnManager.Application.Abstractions;

/// <summary>
/// What IIS's configuration says about a site, in detail - its Details page's IIS tab: the site, every binding (with
/// its certificate), and its app pool with how it starts, recycles and fails.
/// </summary>
/// <param name="ConfigPath">Where IIS keeps it: <c>MACHINE/WEBROOT/APPHOST/&lt;site&gt;</c> in applicationHost.config.</param>
/// <param name="PreloadEnabled">The root application's preloadEnabled; null when IIS has no such setting.</param>
public sealed record IisSiteDetails(
    long Id,
    string Name,
    string State,
    string PhysicalPath,
    string ConfigPath,
    bool ServerAutoStart,
    bool? PreloadEnabled,
    IReadOnlyList<IisBindingDetails> Bindings,
    IisPoolDetails? Pool);

/// <param name="Sni">Server Name Indication: the certificate is chosen by host name (sslFlags 1).</param>
/// <param name="CertificateHash">The thumbprint IIS has for an https binding; null for none.</param>
/// <param name="Certificate">That certificate, from the store IIS names; null when there is no thumbprint or it isn't there.</param>
public sealed record IisBindingDetails(
    string Protocol,
    string Address,
    int? Port,
    string Host,
    string BindingInformation,
    bool Sni,
    string? CertificateHash,
    string? CertificateStore,
    IisCertificate? Certificate);

public sealed record IisCertificate(string Subject, string? FriendlyName, string Issuer, DateTime NotBefore, DateTime NotAfter);

/// <summary>The app pool settings a project's Details can change (Edit app pool).</summary>
/// <param name="Runtime">"v4.0", or "" for No Managed Code.</param>
/// <param name="Pipeline">"Integrated" or "Classic".</param>
/// <param name="Identity">"ApplicationPoolIdentity", "NetworkService", "LocalService" or "LocalSystem" - or "SpecificUser", which is
/// kept as it is (its password isn't known here).</param>
/// <param name="IdleTimeout">Zero: never shut down for being idle.</param>
/// <param name="StartMode">"OnDemand" or "AlwaysRunning".</param>
public sealed record IisPoolSettings(string Runtime, string Pipeline, bool Enable32Bit, string Identity, TimeSpan IdleTimeout, string StartMode)
{
    public static IisPoolSettings From(IisPoolDetails pool) =>
        new(pool.Runtime == "No Managed Code" ? "" : pool.Runtime, pool.Pipeline, pool.Enable32Bit, pool.IdentityType, pool.IdleTimeout, pool.StartMode);
}

/// <summary>
/// One of a site's http bindings as Edit host names leaves it: <paramref name="Host"/> on <paramref name="Port"/>, and what it
/// was - null for one added - so DNN's portal alias of the old host and port can follow it.
/// </summary>
/// <param name="Host">Empty: any host name.</param>
public sealed record HttpBindingEdit(string? OldHost, int? OldPort, string Host, int Port);

/// <param name="Account">The Windows account the worker process runs as - <c>IIS APPPOOL\&lt;pool&gt;</c> for ApplicationPoolIdentity.</param>
/// <param name="RecycleInterval">Regular recycling; zero when it is off.</param>
/// <param name="PrivateMemoryLimitKb">Recycled above this much private memory; zero for no limit.</param>
public sealed record IisPoolDetails(
    string Name,
    string? State,
    string Runtime,
    string Pipeline,
    string IdentityType,
    string Account,
    string StartMode,
    TimeSpan IdleTimeout,
    string? IdleTimeoutAction,
    TimeSpan RecycleInterval,
    IReadOnlyList<TimeSpan> RecycleAt,
    long PrivateMemoryLimitKb,
    long VirtualMemoryLimitKb,
    long QueueLength,
    bool Enable32Bit,
    bool RapidFailProtection,
    long RapidFailMaxCrashes,
    TimeSpan RapidFailInterval,
    long MaxProcesses,
    bool LoadUserProfile,
    IReadOnlyList<int> WorkerProcessIds);
