using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IHttpConnectivityChecker
{
    Task<Result<int>> CheckAsync(string url, int timeoutSeconds, CancellationToken ct);
}
