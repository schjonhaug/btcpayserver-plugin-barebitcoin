using System.Net;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

internal sealed class StateMachineHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

public partial class BareBitcoinApiServiceTests
{
    [Fact]
    public async Task CancellationReachesTransportAndRemovesQueuedSignedRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var transportCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        using var http = new HttpClient(new StateMachineHttpHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return StateMachineHttpHandler.Json("{}");
        }));
        var service = new BareBitcoinApiService("c2VjcmV0", Guid.NewGuid().ToString(), http, NullLogger.Instance);
        var first = service.MakeAuthenticatedRequest("POST", "/invoice", cancellation: transportCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queued = service.MakeAuthenticatedRequest("POST", "/invoice", cancellation: queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        transportCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, requests);
    }
}

public partial class BareBitcoinLightningClientTests
{
    [Fact]
    public async Task PreCanceledCreateDoesNotContactProvider()
    {
        var requests = 0;
        var client = CreateClient(new StateMachineHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(StateMachineHttpHandler.Json(CreateInvoiceApiJson()));
        }), new ThrowingInvoiceService(), timeProvider: ValidCreateTimeProvider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateInvoice(
            LightMoney.Satoshis(250_000), "test", TimeSpan.FromMinutes(1), cancellation.Token));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task LastRetryAfterDefersNextPollEvenWithNoRetries()
    {
        var requests = 0;
        var client = CreateClient(new StateMachineHttpHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }), new ThrowingInvoiceService());
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetInvoice("invoice"));
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetInvoice("invoice"));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("INVOICE_STATUS_UNSPECIFIED")]
    [InlineData("INVOICE_STATUS_FUTURE")]
    [InlineData(null)]
    public async Task UnknownStatusDoesNotBecomeUnpaid(string? status)
    {
        var client = CreateClient(new FakeMessageHandler(ApiJson(status!)), new ThrowingInvoiceService());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInvoice("invoice"));
    }

    [Fact]
    public async Task PaidAmountlessWithoutSettlementRemainsTrackedWithoutZeroCredit()
    {
        var tracked = new HashSet<string> { "invoice" };
        var client = CreateClient(new FakeMessageHandler(ApiJson("INVOICE_STATUS_PAID")), new MutableInvoiceService(tracked));
        var invoice = await client.GetInvoice("invoice");
        Assert.NotNull(invoice);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Null(invoice.AmountReceived);
        Assert.Contains("invoice", tracked);
    }

}

public partial class BareBitcoinInvoiceServiceTests
{
    [Fact]
    public async Task RetiredOwnershipSurvivesRestartAndRejectsStaleTracking()
    {
        await using (var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath))
        {
            await service.TrackInvoice(Scope, "retired");
            await service.UntrackInvoice(Scope, "retired");
            await service.TrackInvoice(Scope, "retired");
            Assert.Empty(await service.GetTrackedInvoices(Scope));
        }
        await using var reloaded = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await reloaded.TrackInvoice(OtherScope, "retired");
        Assert.Empty(await reloaded.GetTrackedInvoices(OtherScope));
        Assert.False(await reloaded.CanAccessInvoice(OtherScope, "retired"));
        Assert.True(await reloaded.CanAccessInvoice(Scope, "retired"));
        Assert.Equal(1, (await reloaded.GetHealth()).Retired);
    }

    [Fact]
    public async Task FailedReadCannotOverwriteRecoveryData()
    {
        var writer = new UnreadableWriter();
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);
        await service.TrackInvoice(Scope, "new");
        await service.FlushAsync();
        Assert.Equal(0, writer.Writes);
        Assert.NotNull(service.PersistenceDisabledReason);
    }

    [Fact]
    public async Task ConcurrentDisposeJoinsWriteAndRejectsFurtherMutations()
    {
        var writer = new BlockingDiskWriter(new FileDiskWriter(FilePath));
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);
        await service.TrackInvoice(Scope, "one");
        var flush = service.FlushAsync();
        await writer.EnteredWrite.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var first = service.DisposeAsync().AsTask();
        var second = service.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TrackInvoice(Scope, "late"));
        writer.Release();
        await Task.WhenAll(flush, first, second);
        Assert.Contains("one", File.ReadAllText(FilePath));
    }

    private sealed class UnreadableWriter : IDiskWriter
    {
        public int Writes;
        public string? Read() => throw new IOException("temporary read failure");
        public Task WriteAsync(string content) { Writes++; return Task.CompletedTask; }
    }
}

public partial class BareBitcoinListenerTests
{
    [Fact]
    public async Task PaidResultIsDeliveredBeforeAnotherRequestCompletes()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await service.TrackInvoice(Scope, "paid");
        await service.TrackInvoice(Scope, "slow");
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeLightningClient(async (id, token) =>
        {
            if (id == "slow")
            {
                slowEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return PaidInvoice(id);
        });
        using var listener = new BareBitcoinListener(client, service, Scope, NullLogger.Instance);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("paid", (await listener.WaitInvoice(deadline.Token)).Id);
    }


}

public partial class BareBitcoinLightningConnectionStringHandlerTests
{
    [Fact]
    public void InvalidSecretFailsBeforeAcceptingConnection()
    {
        var handler = CreateHandler();
        Assert.Null(handler.Create(BaseConnectionString.Replace("private-key=c2VjcmV0", "private-key=invalid!"), Network.Main, out var error));
        Assert.Contains("Base64", error);
    }

    [Fact]
    public async Task MissingAccountFailsAsyncValidationWithUsefulError()
    {
        var handler = CreateHandler();
        var client = Assert.IsType<BareBitcoinLightningClient>(handler.Create(
            BaseConnectionString.Replace("account-id=account-123", "account-id=missing"), Network.Main, out var error));
        Assert.Null(error);
        var result = await client.Validate();
        Assert.NotNull(result);
        Assert.Contains("account", result.ErrorMessage);
    }
}

public partial class BareBitcoinBalanceStateTests
{
    [Fact]
    public async Task CacheIsAccountKeyedAndUnavailableIsNotZero()
    {
        using var http = new HttpClient(new StateMachineHttpHandler((_, _) => Task.FromResult(
            StateMachineHttpHandler.Json("""{"accounts":[{"id":"a","availableBtc":1},{"id":"b","availableBtc":2}]}"""))));
        var service = new BareBitcoinBalanceService(new BareBitcoinApiService("c2VjcmV0", "key", http, NullLogger.Instance), NullLogger.Instance);
        var a = await service.GetBalance("a", TestContext.Current.CancellationToken);
        var b = await service.GetBalance("b", TestContext.Current.CancellationToken);
        Assert.Equal(LightMoney.Coins(1), a.OffchainBalance.Local);
        Assert.Equal(LightMoney.Coins(2), b.OffchainBalance.Local);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetBalance("missing", TestContext.Current.CancellationToken));
    }
}

public partial class BareBitcoinInvoiceServiceTests
{
    [Fact]
    public async Task ShutdownRetriesFailureFromAlreadyRunningFlush()
    {
        var writer = new GatedFailureWriter(new FileDiskWriter(FilePath));
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);
        await service.TrackInvoice(Scope, "accepted");
        var flush = service.FlushAsync();
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var shutdown = service.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        writer.Release.TrySetResult();
        await Task.WhenAll(flush, shutdown);
        Assert.Null(service.LastFlushException);
        Assert.Contains("accepted", File.ReadAllText(FilePath));
    }

    private sealed class GatedFailureWriter(IDiskWriter inner) : IDiskWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public string? Read() => inner.Read();
        public async Task WriteAsync(string content)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
                throw new IOException("first in-flight write failed");
            }
            await inner.WriteAsync(content);
        }
    }
}

public partial class BareBitcoinLightningClientTests
{
    [Fact]
    public async Task InvalidResponseRetainsTrackingAndLaterPaidLookupRecovers()
    {
        var tracked = new HashSet<string> { "invoice" };
        var calls = 0;
        var client = CreateClient(new StateMachineHttpHandler((_, _) => Task.FromResult(
            StateMachineHttpHandler.Json(++calls == 1 ? "{}" : ApiJson("INVOICE_STATUS_PAID", ValidCreateBolt11)))), new MutableInvoiceService(tracked));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInvoice("invoice"));
        Assert.Contains("invoice", tracked);
        Assert.Equal(LightningInvoiceStatus.Paid, (await client.GetInvoice("invoice"))!.Status);
    }

    [Fact]
    public async Task ForeignDirectLookupDoesNotContactProviderAfterTerminalCleanup()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using var registry = new BareBitcoinInvoiceService(NullLogger.Instance, path);
            var owner = BareBitcoinInvoiceScope.ForStoreConnection(new Uri("https://api.example.com"), Network.Main, "account", "owner");
            await registry.TrackInvoice(owner, "invoice");
            await registry.UntrackInvoice(owner, "invoice");
            var requests = 0;
            var client = CreateClient(new StateMachineHttpHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(StateMachineHttpHandler.Json(ApiJson("INVOICE_STATUS_PAID")));
            }), registry, accountId: "account", storeId: "foreign");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetInvoice("invoice"));
            Assert.Equal(0, requests);
        }
        finally { File.Delete(path); }
    }
}

public partial class BareBitcoinApiServiceTests
{
    [Fact]
    public async Task InterruptedPostReportsUnknownOutcomeWithTrace()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StateMachineHttpHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return StateMachineHttpHandler.Json("{}");
        }));
        var api = new BareBitcoinApiService("c2VjcmV0", Guid.NewGuid().ToString(), http, NullLogger.Instance);
        var request = api.MakeAuthenticatedRequest("POST", "/invoice", cancellation: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Contains("outcome is unknown", error.Message);
        Assert.Contains("trace", error.Message);
    }
}

public partial class BareBitcoinBalanceStateTests
{
    [Fact]
    public async Task FreshnessUsesFiveSecondMonotonicLifetime()
    {
        var requests = 0;
        var clock = new BalanceClock();
        using var http = new HttpClient(new StateMachineHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(StateMachineHttpHandler.Json("""{"accounts":[{"id":"a","availableBtc":1}]}"""));
        }));
        var service = new BareBitcoinBalanceService(new BareBitcoinApiService("c2VjcmV0", "key", http, NullLogger.Instance), NullLogger.Instance, clock);
        await service.GetBalance("a", TestContext.Current.CancellationToken);
        clock.Seconds = 4;
        await service.GetBalance("a", TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);
        clock.Seconds = 5;
        await service.GetBalance("a", TestContext.Current.CancellationToken);
        Assert.Equal(2, requests);
    }

    private sealed class BalanceClock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}

public partial class BareBitcoinLightningClientTests
{
    [Fact]
    public async Task RetryBudgetExhaustionStillRecordsServerCooldown()
    {
        var requests = 0;
        var client = CreateClient(new StateMachineHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(++requests < 4 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests)
                { Content = new StringContent("retry") };
            if (requests == 4)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }), new ThrowingInvoiceService(), maxRetries: 3);
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetInvoice("invoice", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetInvoice("invoice", TestContext.Current.CancellationToken));
        Assert.Equal(4, requests);
    }
}
