using System;
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
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinLightningClientTests
{
    // Mainnet BOLT11 test vector from the Lightning spec (BOLT #11):
    // Send $1 with payment_hash 0001020304050607080900010203040506070809000102030405060708090102
    private const string TestBolt11 =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    private static string ApiJson(string status) => $$"""
        {
            "invoice": "{{TestBolt11}}",
            "status": "{{status}}",
            "preimage": "deadbeef"
        }
        """;

    private static BareBitcoinLightningClient CreateClient(
        HttpMessageHandler handler,
        IBareBitcoinInvoiceService invoiceService)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
        return new BareBitcoinLightningClient(
            privateKey: "test-private-key",
            publicKey: "test-public-key",
            accountId: "test-account",
            apiEndpoint: new Uri("https://api.example.com"),
            network: Network.Main,
            httpClient: httpClient,
            logger: NullLogger.Instance,
            invoiceService: invoiceService);
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
    public async Task GetInvoice_ReturnsNull_WhenTrackInvoiceThrowsOperationCanceledException()
    {
        // The outer catch (Exception) in GetInvoice currently swallows
        // OperationCanceledException and returns null.
        // This test documents that behavior. See issue #39 note on whether
        // OperationCanceledException should instead propagate.
        var invoiceService = new ThrowingInvoiceService(
            trackException: new OperationCanceledException("token cancelled"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService);

        var result = await client.GetInvoice("inv-3");

        Assert.Null(result);
    }

    private sealed class ThrowingInvoiceService(
        Exception? trackException = null,
        Exception? untrackException = null) : IBareBitcoinInvoiceService
    {
        public Task TrackInvoice(string invoiceId, CancellationToken cancellation = default)
            => trackException is not null
                ? Task.FromException(trackException)
                : Task.CompletedTask;

        public Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default)
            => untrackException is not null
                ? Task.FromException(untrackException)
                : Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
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
}
