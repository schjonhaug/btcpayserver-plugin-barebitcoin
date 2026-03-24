using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinListenerTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private readonly string _tempDir;

    public BareBitcoinListenerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string InvoiceFilePath => Path.Combine(_tempDir, "tracked-invoices.json");

    private static LightningInvoice PaidInvoice(string id) => new()
    {
        Id = id,
        Status = LightningInvoiceStatus.Paid,
        Amount = LightMoney.Satoshis(1000),
        PaymentHash = id,
        PaidAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task PaidInvoice_IsDeliveredAndUntracked()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await listener.WaitInvoice(cts.Token);

        Assert.Equal("inv-1", result.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, result.Status);

        // Invoice should be untracked after delivery
        var remaining = await invoiceService.GetTrackedInvoices();
        Assert.DoesNotContain("inv-1", remaining);
    }

    [Fact]
    public async Task ChannelFull_BackpressuresInsteadOfDropping()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        // Track a single invoice initially to avoid HashSet iteration order issues
        await invoiceService.TrackInvoice("inv-1");

        // Signal when the first invoice has been written to the channel
        var firstWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceCount = 0;

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            if (invoiceId == "inv-1")
            {
                var count = Interlocked.Increment(ref invoiceCount);
                if (count == 1)
                    firstWritten.TrySetResult();
            }
            return Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId));
        });

        // Capacity 1: only one invoice fits before WriteAsync blocks
        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Wait for first invoice to be written, then add second before reading
        await firstWritten.Task.WaitAsync(TestTimeout);
        await invoiceService.TrackInvoice("inv-2");

        // Read both invoices — neither should be dropped
        var first = await listener.WaitInvoice(cts.Token);
        Assert.NotNull(first);

        var second = await listener.WaitInvoice(cts.Token);
        Assert.NotNull(second);

        // Both invoices were delivered, none dropped
        var ids = new[] { first.Id, second.Id }.OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "inv-1", "inv-2" }, ids);
    }

    [Fact]
    public async Task CancellationDuringWriteAsync_ShutsDownGracefully()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        // Two invoices: in a single polling cycle, the first fills the channel
        // and the second blocks on WriteAsync.
        await invoiceService.TrackInvoice("inv-1");
        await invoiceService.TrackInvoice("inv-2");

        // Signal when the second GetInvoice call happens — the first write
        // has already filled the channel, so the second WriteAsync will block.
        var secondGetInvoice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollCount = 0;

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            var count = Interlocked.Increment(ref pollCount);
            if (count >= 2)
                secondGetInvoice.TrySetResult();
            return Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId));
        });

        // Capacity 1: first paid invoice fills the channel, second blocks
        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1);

        // Wait until the second GetInvoice has been called
        await secondGetInvoice.Task.WaitAsync(TestTimeout);

        // Dispose cancels _cts, which should unblock WriteAsync and shut down cleanly
        listener.Dispose();

        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task Dispose_WhilePolling_ExitsGracefully()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        // Signal when the polling loop reaches GetInvoice
        var reachedGetInvoice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = new TaskCompletionSource<LightningInvoice?>();

        var client = new FakeLightningClient((_, ct) =>
        {
            reachedGetInvoice.TrySetResult();
            ct.Register(() => blocker.TrySetCanceled(ct));
            return blocker.Task;
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        // Wait deterministically for the polling loop to reach GetInvoice
        await reachedGetInvoice.Task.WaitAsync(TestTimeout);

        // Dispose should cancel the token, unblocking the fake, and shut down
        listener.Dispose();

        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task CustomMaxPollConcurrency_IsRespected()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        for (var i = 0; i < 20; i++)
            await invoiceService.TrackInvoice($"inv-{i}");

        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        var allPolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polledCount = 0;

        var client = new FakeLightningClient(async (invoiceId, ct) =>
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            // Record the high-water mark
            int observed;
            do
            {
                observed = Volatile.Read(ref maxObservedConcurrency);
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref maxObservedConcurrency, current, observed) != observed);

            await Task.Delay(50, ct); // simulate work
            Interlocked.Decrement(ref currentConcurrency);

            if (Interlocked.Increment(ref polledCount) >= 20)
                allPolled.TrySetResult();

            return new LightningInvoice
            {
                Id = invoiceId, Status = LightningInvoiceStatus.Unpaid,
                Amount = LightMoney.Satoshis(1000), PaymentHash = invoiceId
            };
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance,
            channelCapacity: 100, maxPollConcurrency: 3);

        await allPolled.Task.WaitAsync(TestTimeout);
        listener.Dispose();

        Assert.True(maxObservedConcurrency <= 3, $"Max concurrency was {maxObservedConcurrency}, expected <= 3");
        Assert.True(maxObservedConcurrency >= 1, "Should have had at least 1 concurrent call");
    }

    [Fact]
    public async Task AdaptiveBackoff_IncreasesDelayOnHighErrorRate()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");
        await invoiceService.TrackInvoice("inv-2");

        var cycleCount = 0;
        var thirdCycleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((_, _) =>
        {
            // Count cycles based on first invoice polled per cycle
            var count = Interlocked.Increment(ref cycleCount);
            if (count >= 6) // 3 cycles * 2 invoices = 6 calls
                thirdCycleStarted.TrySetResult();
            throw new Exception("Simulated backend failure");
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance,
            channelCapacity: 100, maxPollConcurrency: 10);

        await thirdCycleStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(listener.CurrentPollDelay > TimeSpan.FromSeconds(2),
            $"Expected backoff > 2s, got {listener.CurrentPollDelay.TotalSeconds}s");

        listener.Dispose();
    }

    [Fact]
    public async Task AdaptiveBackoff_RecoversWhenErrorsStop()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        var callCount = 0;
        var recoveryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count <= 3)
                throw new Exception("Simulated failure");

            // After 3 failures, start succeeding
            return Task.FromResult<LightningInvoice?>(new LightningInvoice
            {
                Id = invoiceId, Status = LightningInvoiceStatus.Unpaid,
                Amount = LightMoney.Satoshis(1000), PaymentHash = invoiceId
            });
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance,
            channelCapacity: 100, maxPollConcurrency: 10);

        // Wait for backoff to engage and then recover
        // Poll until CurrentPollDelay returns to base or timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sawBackoff = false;
        while (!cts.Token.IsCancellationRequested)
        {
            if (listener.CurrentPollDelay > TimeSpan.FromSeconds(2))
                sawBackoff = true;
            if (sawBackoff && listener.CurrentPollDelay <= TimeSpan.FromSeconds(2))
            {
                recoveryObserved.TrySetResult();
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        await recoveryObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(sawBackoff, "Should have observed backoff");
        Assert.Equal(TimeSpan.FromSeconds(2), listener.CurrentPollDelay);

        listener.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidChannelCapacity_ThrowsArgumentOutOfRange(int capacity)
    {
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        var client = new FakeLightningClient((_, _) => throw new NotImplementedException());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: capacity));
        Assert.Equal("channelCapacity", ex.ParamName);
    }

    /// <summary>
    /// Minimal ILightningClient implementation for testing BareBitcoinListener.
    /// Only GetInvoice(string, CancellationToken) is functional; all other methods throw.
    /// </summary>
    private sealed class FakeLightningClient(
        Func<string, CancellationToken, Task<LightningInvoice?>> getInvoice) : ILightningClient
    {
        public Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
            => getInvoice(invoiceId, cancellation);

        public Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningPayment?> GetPayment(string paymentHash, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
            => throw new NotImplementedException();
        public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
            => throw new NotImplementedException();
    }
}
