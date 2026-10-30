using System.Net;
using System.Net.Sockets;
using DnnManager.Infrastructure.Github;

namespace DnnManager.IntegrationTests;

/// <summary>A site that doesn't answer in time is one that may still be starting - not HttpClient's message.</summary>
[TestClass]
public sealed class HttpConnectivityCheckerTests
{
    [TestMethod]
    public async Task ASiteThatDoesNotAnswerInTime_MayStillBeStarting()
    {
        // Takes the connection and never answers, as IIS does while DNN compiles the site.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await new HttpConnectivityChecker().CheckAsync($"http://127.0.0.1:{port}/", 1, CancellationToken.None);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "first start");
            Assert.IsFalse(result.Error!.Contains("HttpClient", StringComparison.Ordinal), result.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ACancel_IsNotReportedAsAnUnansweredSite()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var result = await new HttpConnectivityChecker().CheckAsync($"http://127.0.0.1:{port}/", 30, cancel.Token);

            Assert.IsFalse(result.Success);
            Assert.IsFalse(result.Error!.Contains("first start", StringComparison.Ordinal), result.Error);
        }
        finally
        {
            listener.Stop();
        }
    }
}
