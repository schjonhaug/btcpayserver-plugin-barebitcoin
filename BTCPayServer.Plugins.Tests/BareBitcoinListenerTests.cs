using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinListenerTests
{
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
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance);
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
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance);
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
        await firstWritten.Task;
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
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance);
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
        await secondGetInvoice.Task;

        // Dispose cancels _cts, which should unblock WriteAsync and shut down cleanly
        listener.Dispose();

        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task Dispose_WhilePolling_ExitsGracefully()
    {
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance);
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
        await reachedGetInvoice.Task;

        // Dispose should cancel the token, unblocking the fake, and shut down
        listener.Dispose();

        Assert.True(listener.IsDisposed);
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
