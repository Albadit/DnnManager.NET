using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Github;

/// <summary>Whether an address answers - the status code it gives, or why it couldn't be reached.</summary>
public sealed class HttpConnectivityChecker : IHttpConnectivityChecker
{
    public async Task<Result<int>> CheckAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await http.GetAsync(url, ct);
            return Result<int>.Ok((int)resp.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout. Not a failure yet: DNN compiles the site on its first request.
            return Result<int>.Fail($"no answer within {timeoutSeconds} seconds - a site's first start can take a few minutes");
        }
        catch (Exception ex)
        {
            return Result<int>.Fail(ex.Message);
        }
    }
}
