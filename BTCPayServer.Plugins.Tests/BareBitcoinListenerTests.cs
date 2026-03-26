using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    public async Task ExpiredInvoice_IsUntrackedWithoutDelivery()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(new LightningInvoice
            {
                Id = invoiceId,
                Status = LightningInvoiceStatus.Expired,
                Amount = LightMoney.Satoshis(1000),
                PaymentHash = invoiceId
            }));

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        await WaitUntilInvoiceIsUntracked(invoiceService, "inv-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<OperationCanceledException>(() => listener.WaitInvoice(cts.Token));
    }

    [Fact]
    public async Task MissingInvoice_IsUntrackedWithoutDelivery()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        var client = new FakeLightningClient((_, _) =>
            Task.FromResult<LightningInvoice?>(null));

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        await WaitUntilInvoiceIsUntracked(invoiceService, "inv-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<OperationCanceledException>(() => listener.WaitInvoice(cts.Token));
    }

    [Fact]
    public async Task ChannelFull_BackpressuresInsteadOfDropping()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        // Track a single invoice initially to avoid HashSet iteration order issues
        await invoiceService.TrackInvoice("inv-1");

        // Signal after the first invoice has actually been written to the channel
        var firstWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        // Capacity 1: only one invoice fits before WriteAsync blocks
        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1,
            onAfterWrite: invoice =>
            {
                if (invoice.Id == "inv-1")
                    firstWritten.TrySetResult();
            });

        using var cts = new CancellationTokenSource(TestTimeout);

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

        // Signal when the second WriteAsync is about to execute — the first write
        // has already filled the channel, so the second WriteAsync will block.
        var secondWriteBlocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        // Capacity 1: first paid invoice fills the channel, second blocks
        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1,
            onBeforeWrite: _ =>
            {
                var count = Interlocked.Increment(ref writeCount);
                if (count == 2)
                    secondWriteBlocking.TrySetResult();
            });

        // Wait until the second WriteAsync is about to block
        await secondWriteBlocking.Task.WaitAsync(TestTimeout);

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
        var blocker = new TaskCompletionSource<LightningInvoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (count <= 1)
                throw new Exception("Simulated failure");

            // After 1 failure, start succeeding
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

    [Fact]
    public async Task AdaptiveBackoff_ActivatesOnHttpRequestException()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");
        await invoiceService.TrackInvoice("inv-2");

        var cycleCount = 0;
        var thirdCycleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((_, _) =>
        {
            var count = Interlocked.Increment(ref cycleCount);
            if (count >= 6) // 3 cycles * 2 invoices = 6 calls
                thirdCycleStarted.TrySetResult();
            throw new HttpRequestException("connection refused");
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance,
            channelCapacity: 100, maxPollConcurrency: 10);

        await thirdCycleStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(listener.CurrentPollDelay > TimeSpan.FromSeconds(2),
            $"Expected backoff > 2s, got {listener.CurrentPollDelay.TotalSeconds}s");

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Constructor_InvalidMaxPollConcurrency_ThrowsArgumentOutOfRange(int concurrency)
    {
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        var client = new FakeLightningClient((_, _) => throw new NotImplementedException());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10, maxPollConcurrency: concurrency));
        Assert.Equal("maxPollConcurrency", ex.ParamName);
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
        var blocker = new TaskCompletionSource<LightningInvoice?>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        // Use a synchronization barrier to structurally prove overlapping in-flight calls
        // instead of relying on wall-clock timing (Task.Delay), which is brittle on slow CI.
        var currentInFlight = 0;
        var overlapTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient(async (invoiceId, ct) =>
        {
            var current = Interlocked.Increment(ref currentInFlight);
            try
            {
                if (current >= 2) overlapTcs.TrySetResult();
                await overlapTcs.Task.WaitAsync(ct);
                return PaidInvoice(invoiceId);
            }
            finally
            {
                Interlocked.Decrement(ref currentInFlight);
            }
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
        // The barrier completing proves at least 2 polls were in-flight simultaneously.
        Assert.True(overlapTcs.Task.IsCompletedSuccessfully,
            "Expected at least 2 concurrent in-flight polls to prove parallel execution");
    }

    [Fact]
    public async Task CancellationDuringErrorBackoff_ShutsDownGracefully()
    {
        await using var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await invoiceService.TrackInvoice("inv-1");

        var delayReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeLightningClient((_, _) =>
        {
            throw new Exception("Simulated failure");
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance,
            channelCapacity: 10,
            onPollCycleCompleted: () => delayReached.TrySetResult());

        // Wait deterministically until the poll cycle completes and is about to enter the delay
        await delayReached.Task.WaitAsync(TestTimeout);

        // Dispose triggers cancellation during the inter-cycle delay
        listener.Dispose();

        Assert.True(listener.IsDisposed);
        Assert.Equal(TaskStatus.RanToCompletion, listener.PollingTask.Status);
    }

    private static async Task WaitUntilInvoiceIsUntracked(BareBitcoinInvoiceService invoiceService, string invoiceId)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var trackedInvoices = await invoiceService.GetTrackedInvoices();
            if (!trackedInvoices.Contains(invoiceId))
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Invoice {invoiceId} was still tracked after {TestTimeout.TotalSeconds} seconds.");
    }

    [Fact]
    public async Task UntrackInvoice_IOException_NullInvoice_ContinuesProcessing()
    {
        await using var realService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await realService.TrackInvoice("inv-null");
        await realService.TrackInvoice("inv-ok");

        var faultyService = new FaultyUntrackInvoiceService(realService, faultyInvoiceId: "inv-null");

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            if (invoiceId == "inv-null")
                return Task.FromResult<LightningInvoice?>(null);
            return Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId));
        });

        using var listener = new BareBitcoinListener(client, faultyService, NullLogger.Instance, channelCapacity: 10);
        using var cts = new CancellationTokenSource(TestTimeout);

        var result = await listener.WaitInvoice(cts.Token);
        Assert.Equal("inv-ok", result.Id);

        // inv-null should still be tracked because untrack failed
        var remaining = await realService.GetTrackedInvoices();
        Assert.Contains("inv-null", remaining);
    }

    [Fact]
    public async Task UntrackInvoice_IOException_ExpiredInvoice_ContinuesProcessing()
    {
        await using var realService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await realService.TrackInvoice("inv-expired");
        await realService.TrackInvoice("inv-ok");

        var faultyService = new FaultyUntrackInvoiceService(realService, faultyInvoiceId: "inv-expired");

        var client = new FakeLightningClient((invoiceId, _) =>
        {
            if (invoiceId == "inv-expired")
                return Task.FromResult<LightningInvoice?>(new LightningInvoice
                {
                    Id = invoiceId,
                    Status = LightningInvoiceStatus.Expired,
                    Amount = LightMoney.Satoshis(1000),
                    PaymentHash = invoiceId
                });
            return Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId));
        });

        using var listener = new BareBitcoinListener(client, faultyService, NullLogger.Instance, channelCapacity: 10);
        using var cts = new CancellationTokenSource(TestTimeout);

        var result = await listener.WaitInvoice(cts.Token);
        Assert.Equal("inv-ok", result.Id);

        // inv-expired should still be tracked because untrack failed
        var remaining = await realService.GetTrackedInvoices();
        Assert.Contains("inv-expired", remaining);
    }

    [Fact]
    public async Task PaidInvoice_NotDeliveredTwice_WhenUntrackFails()
    {
        await using var realService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await realService.TrackInvoice("inv-paid");

        // UntrackInvoice always throws, so the invoice stays tracked across poll cycles
        var faultyService = new FaultyUntrackInvoiceService(realService, faultyInvoiceId: "inv-paid");

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        using var listener = new BareBitcoinListener(client, faultyService, NullLogger.Instance, channelCapacity: 10);
        using var cts = new CancellationTokenSource(TestTimeout);

        // First delivery should succeed
        var first = await listener.WaitInvoice(cts.Token);
        Assert.Equal("inv-paid", first.Id);

        // Second WaitInvoice should time out — the invoice must not be delivered again
        using var shortCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<OperationCanceledException>(() => listener.WaitInvoice(shortCts.Token));
    }

    [Fact]
    public async Task UntrackInvoice_IOException_DoesNotAbortRemainingInvoices()
    {
        await using var realService = new BareBitcoinInvoiceService(NullLogger.Instance, InvoiceFilePath);
        await realService.TrackInvoice("inv-fail-untrack");
        await realService.TrackInvoice("inv-ok");

        // Wrap the real service so UntrackInvoice throws IOException for one invoice
        var faultyService = new FaultyUntrackInvoiceService(realService, faultyInvoiceId: "inv-fail-untrack");

        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        using var listener = new BareBitcoinListener(client, faultyService, NullLogger.Instance, channelCapacity: 10);
        using var cts = new CancellationTokenSource(TestTimeout);

        // Both invoices should be delivered despite IOException on untrack
        var received = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            var invoice = await listener.WaitInvoice(cts.Token);
            received.Add(invoice.Id);
        }

        Assert.Contains("inv-fail-untrack", received);
        Assert.Contains("inv-ok", received);
    }

    /// <summary>
    /// Wraps a real IBareBitcoinInvoiceService, throwing IOException from UntrackInvoice
    /// for a specific invoice ID to simulate disk failures.
    /// </summary>
    private sealed class FaultyUntrackInvoiceService(IBareBitcoinInvoiceService inner, string faultyInvoiceId) : IBareBitcoinInvoiceService
    {
        public Task TrackInvoice(string invoiceId, CancellationToken cancellation = default)
            => inner.TrackInvoice(invoiceId, cancellation);

        public Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default)
        {
            if (invoiceId == faultyInvoiceId)
                throw new IOException("Simulated disk failure");
            return inner.UntrackInvoice(invoiceId, cancellation);
        }

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default)
            => inner.GetTrackedInvoices(cancellation);
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
