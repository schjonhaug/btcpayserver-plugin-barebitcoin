using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinInvoiceLifecycleTests
{
    // Official BOLT #11 vectors. Both were signed at Unix timestamp 1496314658.
    // The amountless vector uses BOLT 11's default one-hour expiry.
    private const string AmountlessBolt11 =
        "lnbc1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq9qrsgq357wnc5r2ueh7ck6q93dj32dlqnls087fxdwk8qakdyafkq3yap9us6v52vjjsrvywa6rt52cm9r9zqt8r2t7mlcwspyetp5h2tztugp9lfyql";

    // 0.0025 BTC with an explicit 60-second expiry.
    private const string AmountBearingBolt11 =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh";

    private const string TestPrivateKey = "dGVzdC1wcml2YXRlLWtleS1mb3ItaG1hYw==";
    private static readonly Uri ApiEndpoint = new("https://api.example.com");

    [Fact]
    public void OfficialBolt11Fixtures_ExposeEncodedAmountsAndLifetimes()
    {
        Assert.Equal(LightMoney.Zero, Parse(AmountlessBolt11).MinimumAmount);
        Assert.Equal(LightMoney.Satoshis(250_000), Parse(AmountBearingBolt11).MinimumAmount);
        Assert.Equal(TimeSpan.FromHours(1), EncodedLifetime(AmountlessBolt11));
        Assert.Equal(TimeSpan.FromMinutes(1), EncodedLifetime(AmountBearingBolt11));
    }

    [Fact]
    public async Task CreateInvoice_AmountlessBolt11ForFixedAmountFailsWithoutTracking()
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(CreateResponse("amountless-id", AmountlessBolt11)),
            invoiceService,
            timeProvider: AtInvoiceTimestamp(AmountlessBolt11));
        var requestedAmount = LightMoney.Satoshis(1_234);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(requestedAmount, "amountless", TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken));

        Assert.Contains("amount", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
    }

    [Fact]
    public async Task CreateInvoice_DifferentBolt11AmountFailsWithoutTracking()
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(CreateResponse("different-amount-id", AmountBearingBolt11)),
            invoiceService,
            timeProvider: AtInvoiceTimestamp(AmountBearingBolt11));
        var requestedAmount = LightMoney.Satoshis(9_999);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(requestedAmount, "different amount", TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken));

        Assert.Contains("amount", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    public async Task CreateInvoice_IncompatibleProviderExpiryFailsWithoutTracking(int requestedExpirySeconds)
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(CreateResponse("expiry-id", AmountBearingBolt11)),
            invoiceService,
            timeProvider: AtInvoiceTimestamp(AmountBearingBolt11));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(
                LightMoney.Satoshis(250_000),
                "expiry",
                TimeSpan.FromSeconds(requestedExpirySeconds)),
            TestContext.Current.CancellationToken));

        Assert.Contains("monitoring deadline", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unsupported-id")]
    public async Task CreateInvoice_UsesBolt11AsDocumentedLookupIdentifier(string? providerId)
    {
        const string accountId = "fallback-owner";
        var invoiceService = new RecordingInvoiceService();
        var response = new JObject { ["invoice"] = AmountlessBolt11 };
        if (providerId is not null)
            response["depositDestinationId"] = providerId;

        var client = CreateClient(
            new StaticResponseHandler(response.ToString(Formatting.None)),
            invoiceService,
            accountId,
            AtInvoiceTimestamp(AmountlessBolt11));
        var paymentHash = Parse(AmountlessBolt11).PaymentHash!.ToString();

        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Zero, "fallback", TimeSpan.FromHours(1)),
            TestContext.Current.CancellationToken);

        var scope = Scope(accountId);
        Assert.Equal(AmountlessBolt11, invoice.Id);
        Assert.Equal(paymentHash, invoice.PaymentHash);
        Assert.Equal([AmountlessBolt11], await invoiceService.GetTrackedInvoices(
            scope, TestContext.Current.CancellationToken));
        Assert.Equal(new ScopedInvoiceCall(scope, AmountlessBolt11), Assert.Single(invoiceService.TrackCalls));
    }

    [Fact]
    public async Task CreateInvoice_AlreadyExpiredBolt11FailsWithoutTracking()
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(CreateResponse("expired-id", AmountBearingBolt11)),
            invoiceService,
            timeProvider: new FixedTimeProvider(Parse(AmountBearingBolt11).ExpiryDate));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(250_000), "expired", TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
    }

    [Fact]
    public async Task CreateInvoice_AmountBearingBolt11ForTopUpFailsWithoutTracking()
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(CreateResponse("fixed-id", AmountBearingBolt11)),
            invoiceService,
            timeProvider: AtInvoiceTimestamp(AmountBearingBolt11));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Zero, "top-up", TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken));

        Assert.Contains("amount-bearing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
    }

    [Theory]
    [InlineData("{}", typeof(InvalidOperationException))]
    [InlineData("{\"invoice\":null}", typeof(InvalidOperationException))]
    [InlineData("{\"invoice\":\"\"}", typeof(InvalidOperationException))]
    [InlineData("not valid json {{{", typeof(JsonReaderException))]
    [InlineData("{\"depositDestinationId\":\"bad-id\",\"invoice\":\"not-a-bolt11\"}", typeof(FormatException))]
    public async Task CreateInvoice_InvalidProviderResponseFailsWithoutTracking(
        string response,
        Type expectedExceptionType)
    {
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(new StaticResponseHandler(response), invoiceService);

        var exception = await Record.ExceptionAsync(() => client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(800), "invalid", TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken));

        Assert.NotNull(exception);
        Assert.IsType(expectedExceptionType, exception);
        Assert.Empty(invoiceService.TrackCalls);
        Assert.Empty(await invoiceService.GetTrackedInvoices(
            Scope("test-account"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("INVOICE_STATUS_CANCELED", LightningInvoiceStatus.Expired, false, true)]
    [InlineData("INVOICE_STATUS_PENDING", LightningInvoiceStatus.Unpaid, true, false)]
    [InlineData("INVOICE_STATUS_UNSPECIFIED", LightningInvoiceStatus.Unpaid, true, false)]
    [InlineData(null, LightningInvoiceStatus.Unpaid, true, false)]
    [InlineData("INVOICE_STATUS_FUTURE", LightningInvoiceStatus.Unpaid, true, false)]
    public async Task GetInvoice_MapsStatusAndAppliesScopedTrackingSideEffects(
        string? providerStatus,
        LightningInvoiceStatus expectedStatus,
        bool expectedTrack,
        bool expectedUntrack)
    {
        const string invoiceId = "status-id";
        const string accountId = "status-owner";
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(GetResponse(AmountlessBolt11, providerStatus)),
            invoiceService,
            accountId);

        var invoice = await client.GetInvoice(invoiceId, TestContext.Current.CancellationToken);

        Assert.NotNull(invoice);
        Assert.Equal(expectedStatus, invoice.Status);
        var call = new ScopedInvoiceCall(Scope(accountId), invoiceId);
        Assert.Equal(expectedTrack ? [call] : [], invoiceService.TrackCalls);
        Assert.Equal(expectedUntrack ? [call] : [], invoiceService.UntrackCalls);
        Assert.Equal(expectedUntrack ? [call] : [], invoiceService.ClaimCalls);
    }

    [Theory]
    [InlineData(AmountlessBolt11, 0L)]
    [InlineData(AmountBearingBolt11, 250_000L)]
    public async Task GetInvoice_ExposesParsedBolt11Amount(string bolt11, long expectedSatoshis)
    {
        const string invoiceId = "amount-id";
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(GetResponse(bolt11, "INVOICE_STATUS_UNPAID")),
            invoiceService);

        var invoice = await client.GetInvoice(invoiceId, TestContext.Current.CancellationToken);

        Assert.NotNull(invoice);
        Assert.Equal(LightMoney.Satoshis(expectedSatoshis), invoice.Amount);
        Assert.Equal(Parse(bolt11).MinimumAmount, invoice.Amount);
        Assert.Equal(
            [new ScopedInvoiceCall(Scope("test-account"), invoiceId)],
            invoiceService.TrackCalls);
        Assert.Empty(invoiceService.UntrackCalls);
    }

    [Fact]
    public async Task GetInvoice_DifferentPaymentHashFailsWithoutTrackingSideEffects()
    {
        const string trackedPaymentHash =
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var invoiceService = new RecordingInvoiceService();
        var client = CreateClient(
            new StaticResponseHandler(GetResponse(AmountBearingBolt11, "INVOICE_STATUS_PAID")),
            invoiceService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInvoice(
            trackedPaymentHash,
            TestContext.Current.CancellationToken));

        Assert.Contains("different invoice", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoiceService.TrackCalls);
        Assert.Empty(invoiceService.UntrackCalls);
        Assert.Empty(invoiceService.ClaimCalls);
    }

    [Fact]
    public async Task GetInvoice_PaidWithoutPreimageFallsBackToPaymentHashAndStaysTracked()
    {
        const string invoiceId = "paid-id";
        const string accountId = "paid-owner";
        var scope = Scope(accountId);
        var invoiceService = new RecordingInvoiceService();
        await invoiceService.TrackInvoice(scope, invoiceId, TestContext.Current.CancellationToken);
        invoiceService.ClearCalls();
        var client = CreateClient(
            new StaticResponseHandler(GetResponse(AmountBearingBolt11, "INVOICE_STATUS_PAID")),
            invoiceService,
            accountId);
        var paymentHash = Parse(AmountBearingBolt11).PaymentHash!.ToString();

        var invoice = await client.GetInvoice(invoiceId, TestContext.Current.CancellationToken);

        Assert.NotNull(invoice);
        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(paymentHash, invoice.Preimage);
        Assert.Equal(Parse(AmountBearingBolt11).MinimumAmount, invoice.AmountReceived);
        Assert.Equal([invoiceId], await invoiceService.GetTrackedInvoices(
            scope, TestContext.Current.CancellationToken));
        Assert.Empty(invoiceService.TrackCalls);
        Assert.Empty(invoiceService.UntrackCalls);
        Assert.Equal([new ScopedInvoiceCall(scope, invoiceId)], invoiceService.ClaimCalls);
    }

    [Fact]
    public async Task GetInvoice_PaidWithPreimagePreservesProviderPreimageAndStaysTracked()
    {
        const string invoiceId = "paid-with-preimage-id";
        const string accountId = "paid-with-preimage-owner";
        const string providerPreimage = "provider-preimage";
        var scope = Scope(accountId);
        var invoiceService = new RecordingInvoiceService();
        await invoiceService.TrackInvoice(scope, invoiceId, TestContext.Current.CancellationToken);
        invoiceService.ClearCalls();
        var client = CreateClient(
            new StaticResponseHandler(GetResponse(
                AmountBearingBolt11,
                "INVOICE_STATUS_PAID",
                providerPreimage)),
            invoiceService,
            accountId);

        var invoice = await client.GetInvoice(invoiceId, TestContext.Current.CancellationToken);

        Assert.NotNull(invoice);
        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(providerPreimage, invoice.Preimage);
        Assert.Equal(Parse(AmountBearingBolt11).MinimumAmount, invoice.AmountReceived);
        Assert.Equal([invoiceId], await invoiceService.GetTrackedInvoices(
            scope, TestContext.Current.CancellationToken));
        Assert.Empty(invoiceService.TrackCalls);
        Assert.Empty(invoiceService.UntrackCalls);
        Assert.Equal([new ScopedInvoiceCall(scope, invoiceId)], invoiceService.ClaimCalls);
    }

    [Fact]
    public async Task TwoConnections_ReloadAndDeliverOnlyTheirOwnPaidInvoices()
    {
        const string accountA = "account-a";
        const string accountB = "account-b";
        const string invoiceAId = "invoice-a";
        const string invoiceBId = "invoice-b";
        var invoiceABolt11 = AmountBearingBolt11;
        var invoiceBBolt11 = AmountBearingBolt11.ToUpperInvariant();
        var scopeA = Scope(accountA);
        var scopeB = Scope(accountB);
        var providerA = new LifecycleProviderHandler(invoiceAId, invoiceABolt11);
        var providerB = new LifecycleProviderHandler(invoiceBId, invoiceBBolt11);
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            await using (var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath))
            {
                var clientA = CreateClient(
                    providerA,
                    invoiceService,
                    accountA,
                    AtInvoiceTimestamp(invoiceABolt11));
                var clientB = CreateClient(
                    providerB,
                    invoiceService,
                    accountB,
                    AtInvoiceTimestamp(invoiceBBolt11));

                var created = await Task.WhenAll(
                    clientA.CreateInvoice(
                        new CreateInvoiceParams(LightMoney.Satoshis(250_000), "A", TimeSpan.FromMinutes(1)),
                        TestContext.Current.CancellationToken),
                    clientB.CreateInvoice(
                        new CreateInvoiceParams(LightMoney.Satoshis(250_000), "B", TimeSpan.FromMinutes(1)),
                        TestContext.Current.CancellationToken));

                Assert.Equal(invoiceABolt11, created[0].Id);
                Assert.Equal(invoiceBBolt11, created[1].Id);
                await invoiceService.FlushAsync();
            }

            await using var reloadedService = new BareBitcoinInvoiceService(NullLogger.Instance, filePath);
            Assert.Equal([invoiceABolt11], await reloadedService.GetTrackedInvoices(
                scopeA, TestContext.Current.CancellationToken));
            Assert.Equal([invoiceBBolt11], await reloadedService.GetTrackedInvoices(
                scopeB, TestContext.Current.CancellationToken));

            var restartedClientA = CreateClient(providerA, reloadedService, accountA);
            var restartedClientB = CreateClient(providerB, reloadedService, accountB);
            var listeners = await StartListenersConcurrently(
                restartedClientA.Listen(TestContext.Current.CancellationToken),
                restartedClientB.Listen(TestContext.Current.CancellationToken));
            Assert.NotSame(listeners[0], listeners[1]);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var delivered = await Task.WhenAll(
                    listeners[0].WaitInvoice(timeout.Token),
                    listeners[1].WaitInvoice(timeout.Token));

                Assert.Equal(invoiceABolt11, delivered[0].Id);
                Assert.Equal(invoiceABolt11, delivered[0].BOLT11);
                Assert.Equal(invoiceBBolt11, delivered[1].Id);
                Assert.Equal(invoiceBBolt11, delivered[1].BOLT11);
                Assert.NotEmpty(providerA.QueriedInvoiceIds);
                Assert.NotEmpty(providerB.QueriedInvoiceIds);
                Assert.All(providerA.QueriedInvoiceIds, id => Assert.Equal(invoiceABolt11, id));
                Assert.All(providerB.QueriedInvoiceIds, id => Assert.Equal(invoiceBBolt11, id));

                await WaitUntilAsync(async () =>
                        (await reloadedService.GetTrackedInvoices(scopeA, timeout.Token)).Count == 0 &&
                        (await reloadedService.GetTrackedInvoices(scopeB, timeout.Token)).Count == 0,
                    timeout.Token);
                Assert.Empty(await reloadedService.GetTrackedInvoices(scopeA, timeout.Token));
                Assert.Empty(await reloadedService.GetTrackedInvoices(scopeB, timeout.Token));
            }
            finally
            {
                listeners[0].Dispose();
                listeners[1].Dispose();
            }
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static BareBitcoinLightningClient CreateClient(
        HttpMessageHandler handler,
        IBareBitcoinInvoiceService invoiceService,
        string accountId = "test-account",
        TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = ApiEndpoint };
        return new BareBitcoinLightningClient(
            TestPrivateKey,
            "test-public-key-" + accountId,
            accountId,
            ApiEndpoint,
            Network.Main,
            httpClient,
            NullLogger.Instance,
            invoiceService,
            maxRetries: 0,
            timeProvider: timeProvider);
    }

    private static BOLT11PaymentRequest Parse(string bolt11) =>
        BOLT11PaymentRequest.Parse(bolt11, Network.Main);

    private static TimeSpan EncodedLifetime(string bolt11)
    {
        var parsed = Parse(bolt11);
        return parsed.ExpiryDate - parsed.Timestamp;
    }

    private static TimeProvider AtInvoiceTimestamp(string bolt11) =>
        new FixedTimeProvider(Parse(bolt11).Timestamp);

    private static BareBitcoinInvoiceScope Scope(string accountId) =>
        BareBitcoinInvoiceScope.ForAccount(ApiEndpoint, Network.Main, accountId);

    private static string CreateResponse(string? invoiceId, string bolt11)
    {
        var response = new JObject { ["invoice"] = bolt11 };
        if (invoiceId is not null)
            response["depositDestinationId"] = invoiceId;
        return response.ToString(Formatting.None);
    }

    private static string GetResponse(string bolt11, string? status, string? preimage = null)
    {
        var response = new JObject { ["invoice"] = bolt11 };
        if (status is not null)
            response["status"] = status;
        if (preimage is not null)
            response["preimage"] = preimage;
        return response.ToString(Formatting.None);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellation)
    {
        while (!await condition())
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation);
    }

    private static async Task<ILightningInvoiceListener[]> StartListenersConcurrently(
        params Task<ILightningInvoiceListener>[] listenerTasks)
    {
        try
        {
            return await Task.WhenAll(listenerTasks);
        }
        catch
        {
            foreach (var listenerTask in listenerTasks)
            {
                if (listenerTask.IsCompletedSuccessfully)
                    listenerTask.Result.Dispose();
            }

            throw;
        }
    }

    private sealed class StaticResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(JsonResponse(response));
    }

    private sealed class LifecycleProviderHandler(string invoiceId, string bolt11) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _queriedInvoiceIds = new();

        public string[] QueriedInvoiceIds => _queriedInvoiceIds.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(JsonResponse(CreateResponse(invoiceId, bolt11)));

            var pathSegments = request.RequestUri!.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var requestedInvoiceId = pathSegments.LastOrDefault() ?? string.Empty;
            _queriedInvoiceIds.Enqueue(requestedInvoiceId);
            if (!StringComparer.Ordinal.Equals(bolt11, requestedInvoiceId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(JsonResponse(GetResponse(
                bolt11,
                "INVOICE_STATUS_PAID",
                "provider-preimage-" + invoiceId)));
        }
    }

    private static HttpResponseMessage JsonResponse(string response) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(response, Encoding.UTF8, "application/json")
    };

    private readonly record struct ScopedInvoiceCall(BareBitcoinInvoiceScope Scope, string InvoiceId);

    private sealed class RecordingInvoiceService : IBareBitcoinInvoiceService
    {
        private readonly ConcurrentDictionary<BareBitcoinInvoiceScope, ConcurrentDictionary<string, byte>> _tracked = new();
        private readonly ConcurrentQueue<ScopedInvoiceCall> _trackCalls = new();
        private readonly ConcurrentQueue<ScopedInvoiceCall> _untrackCalls = new();
        private readonly ConcurrentQueue<ScopedInvoiceCall> _claimCalls = new();

        public ScopedInvoiceCall[] TrackCalls => _trackCalls.ToArray();
        public ScopedInvoiceCall[] UntrackCalls => _untrackCalls.ToArray();
        public ScopedInvoiceCall[] ClaimCalls => _claimCalls.ToArray();

        public Task TrackInvoice(
            BareBitcoinInvoiceScope scope,
            string invoiceId,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            _tracked.GetOrAdd(scope, _ => new ConcurrentDictionary<string, byte>())[invoiceId] = 0;
            _trackCalls.Enqueue(new ScopedInvoiceCall(scope, invoiceId));
            return Task.CompletedTask;
        }

        public Task UntrackInvoice(
            BareBitcoinInvoiceScope scope,
            string invoiceId,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_tracked.TryGetValue(scope, out var invoices))
                invoices.TryRemove(invoiceId, out _);
            _untrackCalls.Enqueue(new ScopedInvoiceCall(scope, invoiceId));
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimLegacyInvoice(
            BareBitcoinInvoiceScope scope,
            string invoiceId,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            _claimCalls.Enqueue(new ScopedInvoiceCall(scope, invoiceId));
            return Task.FromResult(false);
        }

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(
            BareBitcoinInvoiceScope scope,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            IReadOnlyCollection<string> result = _tracked.TryGetValue(scope, out var invoices)
                ? invoices.Keys.Order(StringComparer.Ordinal).ToArray()
                : [];
            return Task.FromResult(result);
        }

        public void ClearCalls()
        {
            _trackCalls.Clear();
            _untrackCalls.Clear();
            _claimCalls.Clear();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
