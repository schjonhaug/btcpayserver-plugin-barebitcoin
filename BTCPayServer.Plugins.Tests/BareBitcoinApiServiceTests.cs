using System;
using System.Collections.Concurrent;
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
    public async Task MakeAuthenticatedRequest_ConcurrentSignedPostCalls_DoNotOverlapForSamePublicKey()
    {
        var activeRequests = 0;
        var maxActiveRequests = 0;
        var activeRequestsLock = new object();
        var handler = new AsyncRecordingHandler(async _ =>
        {
            var active = Interlocked.Increment(ref activeRequests);
            lock (activeRequestsLock)
            {
                maxActiveRequests = Math.Max(maxActiveRequests, active);
            }
            await Task.Delay(25);
            Interlocked.Decrement(ref activeRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });
        var httpClient = new HttpClient(handler);
        var publicKey = $"public-key-{Guid.NewGuid()}";
        var service1 = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: publicKey,
            httpClient: httpClient,
            logger: NullLogger.Instance);
        var service2 = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: publicKey,
            httpClient: httpClient,
            logger: NullLogger.Instance);

        const int concurrentRequests = 20;
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(i => (i % 2 == 0 ? service1 : service2).MakeAuthenticatedRequest("POST", "/v1/test", "{}"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(concurrentRequests, handler.Requests.Length);
        Assert.Equal(1, maxActiveRequests);
    }

    [Fact]
    public async Task MakeAuthenticatedRequest_ConcurrentSignedPostCalls_ProduceMillisecondIncreasingNonces()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var httpClient = new HttpClient(handler);
        var service = new BareBitcoinApiService(
            privateKey: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            publicKey: $"public-key-{Guid.NewGuid()}",
            httpClient: httpClient,
            logger: NullLogger.Instance);

        const int concurrentRequests = 20;
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => service.MakeAuthenticatedRequest("POST", "/v1/test", "{}"))
            .ToArray();

        await Task.WhenAll(tasks);

        var nonces = handler.Requests
            .Select(r => long.Parse(r.Headers.GetValues("x-bb-api-nonce").Single()))
            .ToArray();

        Assert.Equal(concurrentRequests, nonces.Distinct().Count());
        Assert.All(nonces, nonce => Assert.True(nonce >= 1_000_000_000_000));
        Assert.True(nonces.SequenceEqual(nonces.OrderBy(n => n)));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public HttpRequestMessage[] Requests => _requests.ToArray();
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class AsyncRecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder = responder;
        public HttpRequestMessage[] Requests => _requests.ToArray();
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            return await _responder(request);
        }
    }
}
