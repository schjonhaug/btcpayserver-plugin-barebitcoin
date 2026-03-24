using System;
using System.Collections.Generic;
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

    [Fact]
    public async Task PartialPollFailure_SuccessfulInvoiceIsDelivered_FailedInvoiceRemainsTracked()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-ok");
        await invoiceService.TrackInvoice("inv-fail");

        // Signal when first poll cycle completes so we can add a second invoice
        // to prove the listener survives the exception and keeps polling
        var firstCycleOkPolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            if (invoiceId == "inv-fail")
                throw new Exception("Simulated API failure");
            if (invoiceId == "inv-ok")
                firstCycleOkPolled.TrySetResult();
            return Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId));
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        using var cts = new CancellationTokenSource(TestTimeout);

        // Wait for first successful invoice
        var first = await listener.WaitInvoice(cts.Token);
        Assert.Equal("inv-ok", first.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, first.Status);

        // Ensure the poll cycle that processed inv-ok has completed
        await firstCycleOkPolled.Task.WaitAsync(TestTimeout);

        // Add a second invoice after the failure — proves the listener survived the exception
        await invoiceService.TrackInvoice("inv-ok-2");
        var second = await listener.WaitInvoice(cts.Token);
        Assert.Equal("inv-ok-2", second.Id);

        // Failed invoice should remain tracked for retry
        var remaining = await invoiceService.GetTrackedInvoices();
        Assert.Contains("inv-fail", remaining);
        Assert.DoesNotContain("inv-ok", remaining);
        Assert.DoesNotContain("inv-ok-2", remaining);
    }

    [Fact]
    public async Task CancellationDuringConcurrentPolling_ShutsDownGracefully()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        for (var i = 1; i <= 5; i++)
            await invoiceService.TrackInvoice($"inv-{i}");

        var pollsStarted = 0;
        var cancellationsObserved = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = new TaskCompletionSource<LightningInvoice?>();

        var client = new FakeLightningClient((_, ct) =>
        {
            var count = Interlocked.Increment(ref pollsStarted);
            if (count >= 5)
                allStarted.TrySetResult();
            ct.Register(() =>
            {
                blocker.TrySetCanceled(ct);
                if (Interlocked.Increment(ref cancellationsObserved) >= 5)
                    allCancelled.TrySetResult();
            });
            return blocker.Task;
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        // Wait until all 5 concurrent polls are in-flight
        await allStarted.Task.WaitAsync(TestTimeout);

        // Dispose cancels _cts, which should propagate to all concurrent polls
        listener.Dispose();

        // Verify cancellation propagated to all in-flight polls
        await allCancelled.Task.WaitAsync(TestTimeout);
        Assert.Equal(5, Volatile.Read(ref cancellationsObserved));
        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentPolling_CompletesInParallel_NotSequentially()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        for (var i = 1; i <= 10; i++)
            await invoiceService.TrackInvoice($"inv-{i}");

        // Track the peak number of concurrently in-flight polls using a barrier pattern.
        // This proves structural concurrency without relying on wall-clock timing.
        var currentInFlight = 0;
        var peakInFlight = 0;

        var client = new FakeLightningClient(async (invoiceId, ct) =>
        {
            var current = Interlocked.Increment(ref currentInFlight);

            // Atomically update peak if we exceeded it
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref peakInFlight);
                if (current <= snapshot) break;
            } while (Interlocked.CompareExchange(ref peakInFlight, current, snapshot) != snapshot);

            // Small delay to keep polls in-flight long enough for overlap
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref currentInFlight);
            return PaidInvoice(invoiceId);
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 20);

        using var cts = new CancellationTokenSource(TestTimeout);
        var received = new HashSet<string>();

        for (var i = 0; i < 10; i++)
        {
            var invoice = await listener.WaitInvoice(cts.Token);
            received.Add(invoice.Id);
        }

        Assert.Equal(10, received.Count);
        // With 10 invoices and MaxPollConcurrency=10, all should be in-flight simultaneously.
        // Assert at least 2 overlapping polls to prove concurrency (sequential would always be 1).
        Assert.True(Volatile.Read(ref peakInFlight) >= 2,
            $"Peak concurrent polls was {peakInFlight}, expected >= 2 to prove parallel execution");
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
