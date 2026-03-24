using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinApiServiceTests
{
    [Fact]
    public async Task MakeAuthenticatedRequest_AddsRequestIndependentTraceHeader()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var service = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: "public-key",
            httpClient: httpClient,
            logger: NullLogger.Instance);

        await service.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts", useSimpleAuth: true);

        var traceHeader = Assert.Single(handler.Requests).Headers.GetValues("x-bb-trace").Single();
        Assert.StartsWith("background+", traceHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAuthenticatedRequest_UsesCustomTracePrefix()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var service = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: "public-key",
            httpClient: httpClient,
            logger: NullLogger.Instance,
            tracePrefix: "my-account-123");

        await service.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts", useSimpleAuth: true);

        var traceHeader = Assert.Single(handler.Requests).Headers.GetValues("x-bb-trace").Single();
        Assert.StartsWith("my-account-123+", traceHeader, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\r\nvalue")]
    [InlineData("bad\nvalue")]
    public async Task MakeAuthenticatedRequest_InvalidTracePrefix_FallsBackToBackground(string invalidPrefix)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var service = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: "public-key",
            httpClient: httpClient,
            logger: NullLogger.Instance,
            tracePrefix: invalidPrefix);

        await service.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts", useSimpleAuth: true);

        var traceHeader = Assert.Single(handler.Requests).Headers.GetValues("x-bb-trace").Single();
        Assert.StartsWith("background+", traceHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeAuthenticatedRequest_ConcurrentCalls_ProduceUniqueNonces()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var service = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: "public-key",
            httpClient: httpClient,
            logger: NullLogger.Instance);

        const int concurrentRequests = 20;
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => service.MakeAuthenticatedRequest("GET", "/v1/test"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(concurrentRequests, handler.Requests.Length);

        var nonces = handler.Requests
            .Select(r => r.Headers.GetValues("x-bb-api-nonce").Single())
            .ToArray();

        Assert.Equal(concurrentRequests, nonces.Distinct().Count());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public HttpRequestMessage[] Requests => _requests.ToArray();
        private readonly System.Collections.Concurrent.ConcurrentBag<HttpRequestMessage> _requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
