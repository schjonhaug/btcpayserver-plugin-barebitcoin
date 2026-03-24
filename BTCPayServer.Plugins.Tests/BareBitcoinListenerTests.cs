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
        await invoiceService.TrackInvoice("inv-1");
        await invoiceService.TrackInvoice("inv-2");

        // Use a gate to control when the second invoice becomes visible.
        // The fake returns inv-1 as Paid immediately. For inv-2, it blocks
        // until we've read inv-1 from the channel.
        var gate = new TaskCompletionSource<bool>();
        var callCount = 0;

        var client = new FakeLightningClient(async (invoiceId, ct) =>
        {
            if (invoiceId == "inv-2")
            {
                var current = Interlocked.Increment(ref callCount);
                if (current == 1)
                {
                    // First poll of inv-2: block until the test reads inv-1
                    await gate.Task;
                }
            }
            return PaidInvoice(invoiceId);
        });

        // Capacity 1: only one invoice fits before WriteAsync blocks
        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Read the first invoice (inv-1 or inv-2 depending on HashSet ordering)
        var first = await listener.WaitInvoice(cts.Token);
        Assert.NotNull(first);

        // Now unblock the gate so the second invoice can be written
        gate.TrySetResult(true);

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
        await invoiceService.TrackInvoice("inv-1");
        await invoiceService.TrackInvoice("inv-2");

        // inv-1 fills the channel (capacity 1). inv-2 will block on WriteAsync.
        var client = new FakeLightningClient((invoiceId, _) =>
            Task.FromResult<LightningInvoice?>(PaidInvoice(invoiceId)));

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 1);

        // Give the polling loop time to fill the channel and block on the second write
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Dispose cancels _cts, which should unblock WriteAsync and shut down cleanly
        listener.Dispose();

        Assert.True(listener.IsDisposed);
    }

    [Fact]
    public async Task Dispose_WhilePolling_ExitsGracefully()
    {
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance);
        await invoiceService.TrackInvoice("inv-1");

        // Block GetInvoice so the polling loop is mid-flight when we dispose
        var blocker = new TaskCompletionSource<LightningInvoice?>();
        var client = new FakeLightningClient((_, ct) =>
        {
            ct.Register(() => blocker.TrySetCanceled(ct));
            return blocker.Task;
        });

        using var listener = new BareBitcoinListener(client, invoiceService, NullLogger.Instance, channelCapacity: 10);

        // Let the polling loop reach GetInvoice (which will block)
        await Task.Delay(TimeSpan.FromSeconds(1));

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
