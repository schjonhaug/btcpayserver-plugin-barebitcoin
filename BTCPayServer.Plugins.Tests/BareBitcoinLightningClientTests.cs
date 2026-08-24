using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinLightningClientTests
{
    // Mainnet BOLT11 test vector from BOLT #11 spec:
    // "Please make a donation of any amount using payment_hash 0001..."
    private const string TestBolt11 =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    // 0.0025 BTC with an explicit 60-second expiry.
    private const string ValidCreateBolt11 =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh";

    private static readonly TimeProvider ValidCreateTimeProvider = new FixedTimeProvider(
        BOLT11PaymentRequest.Parse(ValidCreateBolt11, Network.Main).Timestamp);

    // Valid Base64 key required by BareBitcoinApiService.CreateHmac
    private const string TestPrivateKey = "dGVzdC1wcml2YXRlLWtleS1mb3ItaG1hYw==";

    private static string ApiJson(string status) => $$"""
        {
            "invoice": "{{TestBolt11}}",
            "status": "{{status}}",
            "preimage": "deadbeef"
        }
        """;

    private static string CreateInvoiceApiJson() => $$"""
        {
            "depositDestinationId": "dep-1",
            "invoice": "{{ValidCreateBolt11}}"
        }
        """;

    private static BareBitcoinLightningClient CreateClient(
        HttpMessageHandler handler,
        IBareBitcoinInvoiceService invoiceService,
        ILogger? logger = null,
        int maxRetries = 0,
        string accountId = "test-account",
        string storeId = "test-store",
        TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
        return new BareBitcoinLightningClient(
            privateKey: TestPrivateKey,
            publicKey: "test-public-key",
            accountId: accountId,
            storeId: storeId,
            apiEndpoint: new Uri("https://api.example.com"),
            network: Network.Main,
            httpClient: httpClient,
            logger: logger ?? NullLogger.Instance,
            invoiceService: invoiceService,
            maxRetries: maxRetries,
            timeProvider: timeProvider);
    }

    [Fact]
    public async Task GetInvoice_ReturnsInvoice_WhenTrackInvoiceThrowsIOException()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService);

        var result = await client.GetInvoice("inv-1");

        Assert.NotNull(result);
        Assert.Equal("inv-1", result.Id);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Status);
        Assert.Equal(TestBolt11, result.BOLT11);
    }

    [Fact]
    public async Task OwningStoreStartupPoll_ReclaimsUnpaidLegacyInvoiceIntoItsScope()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(filePath, "[\"legacy-invoice\"]");

        try
        {
            await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath);
            var client = CreateClient(
                new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID")),
                invoiceService,
                accountId: "owner-account");

            var invoice = await client.GetInvoice("legacy-invoice");

            Assert.NotNull(invoice);
            Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
            var ownerScope = BareBitcoinInvoiceScope.ForStoreConnection(
                new Uri("https://api.example.com"), Network.Main, "owner-account", "test-store");
            var foreignScope = BareBitcoinInvoiceScope.ForStoreConnection(
                new Uri("https://api.example.com"), Network.Main, "owner-account", "foreign-store");
            Assert.Equal(["legacy-invoice"], await invoiceService.GetTrackedInvoices(ownerScope));
            Assert.Empty(await invoiceService.GetTrackedInvoices(foreignScope));

            await invoiceService.FlushAsync();
            Assert.Contains("\"unassignedLegacyInvoices\":[]", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task OwningStoreStartupPoll_KeepsPaidLegacyInvoiceTrackedUntilDelivery()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(filePath, "[\"legacy-paid\"]");

        try
        {
            await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath);
            var client = CreateClient(
                new FakeMessageHandler(ApiJson("INVOICE_STATUS_PAID")),
                invoiceService,
                accountId: "owner-account");

            var invoice = await client.GetInvoice("legacy-paid");

            Assert.NotNull(invoice);
            Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
            Assert.NotNull(invoice.PaidAt);
            Assert.NotNull(invoice.AmountReceived);
            var ownerScope = BareBitcoinInvoiceScope.ForStoreConnection(
                new Uri("https://api.example.com"), Network.Main, "owner-account", "test-store");
            var foreignScope = BareBitcoinInvoiceScope.ForStoreConnection(
                new Uri("https://api.example.com"), Network.Main, "owner-account", "foreign-store");
            Assert.Equal(["legacy-paid"], await invoiceService.GetTrackedInvoices(ownerScope));
            Assert.Empty(await invoiceService.GetTrackedInvoices(foreignScope));

            await invoiceService.FlushAsync();
            Assert.Contains("\"unassignedLegacyInvoices\":[]", await File.ReadAllTextAsync(filePath));
            Assert.Contains("legacy-paid", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task OwningStoreStartupPoll_ClearsExpiredLegacyInvoiceFromQuarantine()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(filePath, "[\"legacy-expired\"]");

        try
        {
            await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath);
            var client = CreateClient(
                new FakeMessageHandler(ApiJson("INVOICE_STATUS_EXPIRED")),
                invoiceService,
                accountId: "owner-account");

            var invoice = await client.GetInvoice("legacy-expired");

            Assert.NotNull(invoice);
            Assert.Equal(LightningInvoiceStatus.Expired, invoice.Status);
            await invoiceService.FlushAsync();
            Assert.Contains("\"unassignedLegacyInvoices\":[]", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task GetInvoice_ReturnsInvoice_WhenUntrackInvoiceThrowsIOException()
    {
        var invoiceService = new ThrowingInvoiceService(
            untrackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_EXPIRED"));
        var client = CreateClient(handler, invoiceService);

        var result = await client.GetInvoice("inv-2");

        Assert.NotNull(result);
        Assert.Equal("inv-2", result.Id);
        Assert.Equal(LightningInvoiceStatus.Expired, result.Status);
        Assert.Equal(TestBolt11, result.BOLT11);
    }

    [Fact]
    public async Task GetInvoice_PropagatesOperationCanceledException_FromTrackInvoice()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackException: new OperationCanceledException("token cancelled"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetInvoice("inv-3"));
    }

    [Fact]
    public async Task CreateInvoice_ReturnsInvoice_WhenTrackInvoiceThrowsIOException()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(CreateInvoiceApiJson());
        var client = CreateClient(handler, invoiceService, timeProvider: ValidCreateTimeProvider);

        var result = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1)));

        Assert.NotNull(result);
        Assert.Equal(ValidCreateBolt11, result.Id);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Status);
        Assert.Equal(ValidCreateBolt11, result.BOLT11);
    }

    [Fact]
    public async Task CreateInvoice_TracksInMemoryWhenPersistedSchemaIsUnsupported()
    {
        const string futureState =
            "{\"version\":99,\"scopes\":{\"future-scope\":[\"future-invoice\"]},\"futureField\":true}";
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(filePath, futureState);

        try
        {
            await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath);
            var client = CreateClient(
                new FakeMessageHandler(CreateInvoiceApiJson()),
                invoiceService,
                accountId: "owner-account",
                timeProvider: ValidCreateTimeProvider);

            var result = await client.CreateInvoice(
                new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1)));

            Assert.Equal(ValidCreateBolt11, result.Id);
            var ownerScope = BareBitcoinInvoiceScope.ForStoreConnection(
                new Uri("https://api.example.com"), Network.Main, "owner-account", "test-store");
            Assert.Equal([ValidCreateBolt11], await invoiceService.GetTrackedInvoices(ownerScope));

            await invoiceService.FlushAsync();
            Assert.Equal(futureState, await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task GetInvoice_UsesApiKeyOnlyAuthHeaders()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new RecordingMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService);

        await client.GetInvoice("inv-auth");

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.Contains("x-bb-api-key"));
        Assert.False(request.Headers.Contains("x-bb-api-nonce"));
        Assert.False(request.Headers.Contains("x-bb-api-hmac"));
    }

    [Fact]
    public async Task CreateInvoice_UsesFullHmacAuthHeaders()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new RecordingMessageHandler(CreateInvoiceApiJson());
        var client = CreateClient(handler, invoiceService, timeProvider: ValidCreateTimeProvider);

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1)));

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.Contains("x-bb-api-key"));
        Assert.True(request.Headers.Contains("x-bb-api-nonce"));
        Assert.True(request.Headers.Contains("x-bb-api-hmac"));
    }

    [Fact]
    public async Task CreateInvoice_TrimsAccountIdForApiRequestsAndScopeIdentity()
    {
        var invoiceService = new ScopedInMemoryInvoiceService();
        var handler = new RecordingMessageHandler(CreateInvoiceApiJson());
        var client = CreateClient(
            handler,
            invoiceService,
            accountId: "  account-a  ",
            timeProvider: ValidCreateTimeProvider);

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1)));

        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.Equal("account-a", Newtonsoft.Json.Linq.JObject.Parse(requestBody).Value<string>("accountId"));
        Assert.Contains("account-id=account-a", client.ToString());

        Assert.Contains("store-id=test-store", client.ToString());
        var normalizedScope = BareBitcoinInvoiceScope.ForStoreConnection(
            new Uri("https://api.example.com"), Network.Main, "account-a", "test-store");
        Assert.Equal([ValidCreateBolt11], await invoiceService.GetTrackedInvoices(normalizedScope));
    }

    [Fact]
    public async Task CreateInvoice_PropagatesOperationCanceledException_FromTrackInvoice()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackException: new OperationCanceledException("token cancelled"));

        var handler = new FakeMessageHandler(CreateInvoiceApiJson());
        var client = CreateClient(handler, invoiceService, timeProvider: ValidCreateTimeProvider);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.CreateInvoice(
                new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public async Task GetInvoice_LogsWarning_WhenTrackInvoiceThrowsIOException()
    {
        var logger = new CapturingLogger();
        var invoiceService = new ThrowingInvoiceService(
            trackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService, logger);

        await client.GetInvoice("inv-log-1");

        var warning = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("inv-log-1", warning.Message);
        Assert.IsType<IOException>(warning.Exception);
    }

    [Fact]
    public async Task GetInvoice_LogsWarning_WhenUntrackInvoiceThrowsIOException()
    {
        var logger = new CapturingLogger();
        var invoiceService = new ThrowingInvoiceService(
            untrackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_EXPIRED"));
        var client = CreateClient(handler, invoiceService, logger);

        await client.GetInvoice("inv-log-2");

        var warning = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("inv-log-2", warning.Message);
        Assert.IsType<IOException>(warning.Exception);
    }

    [Fact]
    public async Task CreateInvoice_LogsWarning_WhenTrackInvoiceThrowsIOException()
    {
        var logger = new CapturingLogger();
        var invoiceService = new ThrowingInvoiceService(
            trackException: new IOException("disk full"));

        var handler = new FakeMessageHandler(CreateInvoiceApiJson());
        var client = CreateClient(
            handler,
            invoiceService,
            logger,
            timeProvider: ValidCreateTimeProvider);

        await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1)));

        var warning = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains(ValidCreateBolt11, warning.Message);
        Assert.IsType<IOException>(warning.Exception);
    }

    private sealed class ThrowingInvoiceService(
        Exception? trackException = null,
        Exception? untrackException = null,
        IReadOnlyCollection<string>? trackedInvoices = null) : IBareBitcoinInvoiceService
    {
        public Task TrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
            => trackException is not null
                ? Task.FromException(trackException)
                : Task.CompletedTask;

        public Task UntrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
            => untrackException is not null
                ? Task.FromException(untrackException)
                : Task.CompletedTask;

        public Task<bool> TryClaimLegacyInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(BareBitcoinInvoiceScope scope, CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyCollection<string>>(trackedInvoices ?? Array.Empty<string>());
    }

    [Fact]
    public async Task GetInvoice_ReturnsNull_OnNotFound()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new ThrowingMessageHandler(
            new HttpRequestException("not found", null, HttpStatusCode.NotFound));
        var client = CreateClient(handler, invoiceService);

        var result = await client.GetInvoice("inv-4");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvoice_PropagatesHttpRequestException()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new ThrowingMessageHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetInvoice("inv-5"));
    }

    [Fact]
    public async Task GetInvoice_PropagatesOperationCanceledException()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new ThrowingMessageHandler(new OperationCanceledException("timed out"));
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetInvoice("inv-timeout"));
    }

    [Fact]
    public async Task GetInvoice_ReturnsNull_WhenInvoiceFieldIsMissing()
    {
        var json = """
            {
                "status": "INVOICE_STATUS_UNPAID"
            }
            """;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new FakeMessageHandler(json);
        var client = CreateClient(handler, invoiceService);

        var result = await client.GetInvoice("inv-missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvoice_PropagatesFormatException_FromMalformedBolt11()
    {
        var json = """
            {
                "invoice": "not-a-valid-bolt11",
                "status": "INVOICE_STATUS_UNPAID"
            }
            """;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new FakeMessageHandler(json);
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<FormatException>(
            () => client.GetInvoice("inv-bad-bolt11"));
    }

    [Fact]
    public async Task GetInvoice_PropagatesJsonException()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new FakeMessageHandler("not valid json {{{");
        var client = CreateClient(handler, invoiceService);

        // JObject.Parse throws Newtonsoft JsonReaderException (subclass of JsonException)
        await Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(
            () => client.GetInvoice("inv-6"));
    }

    [Fact]
    public async Task GetInvoice_ThrowsRateLimitedException_WhenBackoffEntryExists()
    {
        var attemptCount = 0;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new PerInvoiceHandler(_ =>
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);
        client.SetRateLimitBackoff("inv-deferred", DateTimeOffset.UtcNow.AddMinutes(5));

        var ex = await Assert.ThrowsAsync<RateLimitedException>(
            () => client.GetInvoice("inv-deferred"));
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter > TimeSpan.Zero, "RetryAfter should be positive");
        Assert.Equal(0, attemptCount);
    }

    [Fact]
    public async Task GetInvoice_RetriesTransientError_ThenSucceeds()
    {
        var attemptCount = 0;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            return attempt == 1
                ? Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("connection refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
                });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.GetInvoice("inv-retry");

        Assert.NotNull(result);
        Assert.Equal("inv-retry", result.Id);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task GetInvoice_ThrowsRateLimitedException_WhenRetryAfterExceedsCap()
    {
        var attemptCount = 0;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new CountingPerInvoiceHandler((_, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var ex = await Assert.ThrowsAsync<RateLimitedException>(
            () => client.GetInvoice("inv-long-wait"));
        Assert.Equal(1, attemptCount);
        Assert.Equal(1, client.RateLimitBackoffCount);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }

    [Fact]
    public async Task GetInvoice_ThrowsHttpRequestException_WhenMaxRetriesExceeded()
    {
        var attemptCount = 0;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new PerInvoiceHandler(_ =>
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connection refused"));
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetInvoice("inv-exhaust"));
        Assert.Equal(4, attemptCount);
    }

    [Fact]
    public async Task GetInvoice_Retries429WithinCap_ThenSucceeds()
    {
        var attemptCount = 0;
        var invoiceService = new ThrowingInvoiceService();
        var handler = new CountingPerInvoiceHandler((_, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromMilliseconds(50));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.GetInvoice("inv-retry-429");

        Assert.NotNull(result);
        Assert.Equal("inv-retry-429", result.Id);
        Assert.Equal(2, attemptCount);
        Assert.Equal(0, client.RateLimitBackoffCount);
    }

    private sealed class FakeMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RecordingMessageHandler(string responseBody) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();
        private readonly ConcurrentQueue<string> _requestBodies = new();
        public HttpRequestMessage[] Requests => _requests.ToArray();
        public string[] RequestBodies => _requestBodies.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            if (request.Content is not null)
                _requestBodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    [Fact]
    public async Task ListInvoices_ReturnsPartialResults_WhenOneInvoiceFails()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", "inv-fail", "inv-ok2" });

        var handler = new PerInvoiceHandler(invoiceId =>
            invoiceId == "inv-fail"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
                }));

        var client = CreateClient(handler, invoiceService);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Equal(2, result.Length);
        Assert.Contains(result, i => i.Id == "inv-ok");
        Assert.Contains(result, i => i.Id == "inv-ok2");
    }

    [Fact]
    public async Task ListInvoices_ReturnsOnlyInvoicesOwnedByStoreConnectionWithSharedProviderAccount()
    {
        var invoiceService = new ScopedInMemoryInvoiceService();
        var storeA = CreateClient(
            new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID")),
            invoiceService,
            accountId: "shared-account",
            storeId: "store-a");
        var storeB = CreateClient(
            new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID")),
            invoiceService,
            accountId: "shared-account",
            storeId: "store-b");

        await storeA.GetInvoice("inv-a");
        await storeB.GetInvoice("inv-b");

        var invoicesA = await storeA.ListInvoices(new ListInvoicesParams());
        var invoicesB = await storeB.ListInvoices(new ListInvoicesParams());

        Assert.Equal(["inv-a"], invoicesA.Select(invoice => invoice.Id));
        Assert.Equal(["inv-b"], invoicesB.Select(invoice => invoice.Id));
    }

    [Fact]
    public async Task ListInvoices_PropagatesOperationCanceledException()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", "inv-cancel" });

        var handler = new PerInvoiceHandler(invoiceId =>
            invoiceId == "inv-cancel"
                ? Task.FromException<HttpResponseMessage>(new OperationCanceledException("cancelled"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
                }));

        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.ListInvoices(new ListInvoicesParams()));
    }

    [Fact]
    public async Task ListInvoices_SkipsMalformedJson()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", "inv-bad-json" });

        var handler = new PerInvoiceHandler(invoiceId =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    invoiceId == "inv-bad-json" ? "not valid json {{{" : ApiJson("INVOICE_STATUS_UNPAID"),
                    Encoding.UTF8, "application/json")
            }));

        var client = CreateClient(handler, invoiceService);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-ok", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_SkipsMalformedBolt11()
    {
        var malformedBolt11Json = """
            {
                "invoice": "not-a-valid-bolt11",
                "status": "INVOICE_STATUS_UNPAID"
            }
            """;

        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", "inv-bad-bolt11" });

        var handler = new PerInvoiceHandler(invoiceId =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    invoiceId == "inv-bad-bolt11" ? malformedBolt11Json : ApiJson("INVOICE_STATUS_UNPAID"),
                    Encoding.UTF8, "application/json")
            }));

        var client = CreateClient(handler, invoiceService);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-ok", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_SkipsInvoiceWithMismatchedPaymentHash()
    {
        const string mismatchedPaymentHash =
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", mismatchedPaymentHash });

        var handler = new PerInvoiceHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            }));

        var client = CreateClient(handler, invoiceService);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-ok", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_PropagatesUnauthorizedHttpException()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-auth-fail" });

        var handler = new PerInvoiceHandler(_ =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)));

        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListInvoices(new ListInvoicesParams()));
    }

    [Fact]
    public async Task ListInvoices_PropagatesForbiddenHttpException()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-forbidden" });

        var handler = new PerInvoiceHandler(_ =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)));

        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListInvoices(new ListInvoicesParams()));
    }

    [Fact]
    public async Task ListInvoices_LogsWarning_WhenInvoiceIsSkipped()
    {
        var logger = new CapturingLogger();
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-fail" });

        var handler = new PerInvoiceHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var client = CreateClient(handler, invoiceService, logger);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Empty(result);
        var warning = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Warning);
        Assert.Contains("inv-fail", warning.Message);
        Assert.IsType<HttpRequestException>(warning.Exception);
    }

    private sealed class PerInvoiceHandler(Func<string, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Extract invoice ID from URL path: /v1/deposit-destinations/bitcoin/invoice/{invoiceId}
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            var invoiceId = segments[^1];
            return handler(invoiceId);
        }
    }

    private sealed class CountingPerInvoiceHandler : HttpMessageHandler
    {
        private readonly Func<string, int, Task<HttpResponseMessage>> _handler;
        private readonly ConcurrentDictionary<string, int> _attempts = new();

        public CountingPerInvoiceHandler(Func<string, int, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            var invoiceId = segments[^1];
            var attempt = _attempts.AddOrUpdate(invoiceId, 1, (_, prev) => prev + 1);
            return _handler(invoiceId, attempt);
        }
    }

    // --- Retry tests ---

    [Fact]
    public async Task ListInvoices_RetriesTransientError_ThenSucceeds()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-retry" });

        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
            attempt == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("server error")
                })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
                }));

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-retry", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_RetriesNetworkError_ThenSucceeds()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-net" });

        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
            attempt == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
                }));

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-net", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_ExhaustsRetries_ThenSkips()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-ok", "inv-always-fail" });

        var failAttemptCount = 0;
        var handler = new CountingPerInvoiceHandler((invoiceId, _) =>
        {
            if (invoiceId == "inv-always-fail")
            {
                Interlocked.Increment(ref failAttemptCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("server error")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 2);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-ok", result[0].Id);
        Assert.Equal(3, failAttemptCount); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ListInvoices_DoesNotRetryAuthErrors()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-auth-fail" });

        var attemptCount = 0;
        var handler = new CountingPerInvoiceHandler((_, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListInvoices(new ListInvoicesParams()));

        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task ListInvoices_DoesNotRetryParsingErrors()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-bad-json" });

        var attemptCount = 0;
        var handler = new PerInvoiceHandler(_ =>
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not valid json {{{", Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Empty(result);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task ListInvoices_Retries429_ThenSucceeds()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-rate-limited" });

        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
        {
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-rate-limited", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_HandlesNegativeRetryAfter_FallsBackToExponentialBackoff()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-stale-header" });

        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
        {
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                // Set a Retry-After date in the past, which would produce a negative TimeSpan
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    DateTimeOffset.UtcNow.AddSeconds(-10));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-stale-header", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_SkipsInvoiceWhenRetryAfterExceedsCap()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-long-wait" });

        var attemptCount = 0;
        var handler = new CountingPerInvoiceHandler((_, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Empty(result);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task ListInvoices_SkipsInvoiceWhenRetryAfterJustAboveCap()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-above-cap" });

        var attemptCount = 0;
        var handler = new CountingPerInvoiceHandler((_, attempt) =>
        {
            Interlocked.Increment(ref attemptCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            // 61 seconds: just above the 60-second cap, should trigger skip
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(61));
            return Task.FromResult(response);
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Empty(result);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task ListInvoices_429WithoutRetryAfterFallsBackToBackoff()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-no-header" });

        var handler = new CountingPerInvoiceHandler((invoiceId, attempt) =>
        {
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                // No Retry-After header set
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        var result = await client.ListInvoices(new ListInvoicesParams());

        Assert.Single(result);
        Assert.Equal("inv-no-header", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_DeferredInvoiceSkippedWithoutApiCall()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-deferred" });

        var attemptCount = 0;
        var handler = new CountingPerInvoiceHandler((_, _) =>
        {
            Interlocked.Increment(ref attemptCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        // First call: hits the API, gets 429 with large Retry-After, records backoff
        var result1 = await client.ListInvoices(new ListInvoicesParams());
        Assert.Empty(result1);
        Assert.Equal(1, attemptCount);

        // Second call: invoice is deferred, no API call made
        var result2 = await client.ListInvoices(new ListInvoicesParams());
        Assert.Empty(result2);
        Assert.Equal(1, attemptCount); // No additional API calls
    }

    [Fact]
    public async Task ListInvoices_SuccessfulFetchClearsBackoff()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackedInvoices: new[] { "inv-recover" });

        var callCount = 0;
        var handler = new CountingPerInvoiceHandler((_, _) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited")
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        // First call: triggers backoff
        var result1 = await client.ListInvoices(new ListInvoicesParams());
        Assert.Empty(result1);
        Assert.Equal(1, callCount);

        // Simulate backoff expiry by setting the timestamp to the past
        client.SetRateLimitBackoff("inv-recover", DateTimeOffset.UtcNow.AddSeconds(-1));

        // Next call: backoff expired, makes API call, succeeds, clears backoff
        var result2 = await client.ListInvoices(new ListInvoicesParams());
        Assert.Single(result2);
        Assert.Equal(2, callCount);
        Assert.Equal(0, client.RateLimitBackoffCount);
    }

    [Fact]
    public async Task ListInvoices_PrunesBackoffForUntrackedInvoices()
    {
        // Start with one tracked invoice
        var trackedSet = new HashSet<string> { "inv-tracked" };
        var invoiceService = new MutableInvoiceService(trackedSet);

        var handler = new CountingPerInvoiceHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApiJson("INVOICE_STATUS_UNPAID"), Encoding.UTF8, "application/json")
            }));

        var client = CreateClient(handler, invoiceService, maxRetries: 3);

        // Seed backoff for both a tracked and an untracked invoice
        client.SetRateLimitBackoff("inv-tracked", DateTimeOffset.UtcNow.AddMinutes(5));
        client.SetRateLimitBackoff("inv-gone", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(2, client.RateLimitBackoffCount);

        // ListInvoices should prune the untracked entry
        await client.ListInvoices(new ListInvoicesParams());

        // "inv-gone" should have been pruned; "inv-tracked" still deferred
        Assert.Equal(1, client.RateLimitBackoffCount);
    }

    private sealed class MutableInvoiceService(HashSet<string> tracked) : IBareBitcoinInvoiceService
    {
        public Task TrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
        {
            tracked.Add(invoiceId);
            return Task.CompletedTask;
        }

        public Task UntrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
        {
            tracked.Remove(invoiceId);
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimLegacyInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(BareBitcoinInvoiceScope scope, CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyCollection<string>>(new HashSet<string>(tracked));
    }

    private sealed class ScopedInMemoryInvoiceService : IBareBitcoinInvoiceService
    {
        private readonly Dictionary<BareBitcoinInvoiceScope, HashSet<string>> _tracked = new();
        private readonly object _lock = new();

        public Task TrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
        {
            lock (_lock)
            {
                if (!_tracked.TryGetValue(scope, out var invoices))
                {
                    invoices = new HashSet<string>();
                    _tracked.Add(scope, invoices);
                }
                invoices.Add(invoiceId);
            }
            return Task.CompletedTask;
        }

        public Task UntrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
        {
            lock (_lock)
            {
                if (_tracked.TryGetValue(scope, out var invoices))
                    invoices.Remove(invoiceId);
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimLegacyInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(BareBitcoinInvoiceScope scope, CancellationToken cancellation = default)
        {
            lock (_lock)
            {
                IReadOnlyCollection<string> invoices = _tracked.TryGetValue(scope, out var tracked)
                    ? new HashSet<string>(tracked)
                    : Array.Empty<string>();
                return Task.FromResult(invoices);
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
