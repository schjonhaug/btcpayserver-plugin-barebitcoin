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
    // Mainnet BOLT11 test vector from BOLT #11 spec:
    // "Please make a donation of any amount using payment_hash 0001..."
    private const string TestBolt11 =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    // Valid Base64 key required by BareBitcoinApiService.CreateHmac
    private const string TestPrivateKey = "dGVzdC1wcml2YXRlLWtleS1mb3ItaG1hYw==";

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
            privateKey: TestPrivateKey,
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
    public async Task GetInvoice_PropagatesOperationCanceledException_FromTrackInvoice()
    {
        var invoiceService = new ThrowingInvoiceService(
            trackException: new OperationCanceledException("token cancelled"));

        var handler = new FakeMessageHandler(ApiJson("INVOICE_STATUS_UNPAID"));
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetInvoice("inv-3"));
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

    [Fact]
    public async Task GetInvoice_PropagatesHttpRequestException()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new ThrowingMessageHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, invoiceService);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetInvoice("inv-4"));
    }

    [Fact]
    public async Task GetInvoice_PropagatesJsonException()
    {
        var invoiceService = new ThrowingInvoiceService();
        var handler = new FakeMessageHandler("not valid json {{{");
        var client = CreateClient(handler, invoiceService);

        // JObject.Parse throws Newtonsoft JsonReaderException (subclass of JsonException)
        await Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(
            () => client.GetInvoice("inv-5"));
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

    private sealed class ThrowingMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}
