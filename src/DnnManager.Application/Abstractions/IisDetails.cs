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
