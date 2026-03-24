using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinLightningConnectionStringHandlerTests
{
    [Fact]
    public void Create_ReturnsClientWithoutHttpContextDependency()
    {
        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new JsonHandler("""
            {
              "accounts": [
                {
                  "id": "account-123",
                  "availableBtc": 0.01
                }
              ]
            }
            """)));
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, Path.GetTempFileName());
        var handler = new BareBitcoinLightningConnectionStringHandler(httpClientFactory, NullLoggerFactory.Instance, invoiceService);

        var client = handler.Create(
            "type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123",
            Network.Main,
            out var error);

        Assert.NotNull(client);
        Assert.Null(error);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        private readonly HttpClient _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class JsonHandler(string responseBody) : HttpMessageHandler
    {
        private readonly string _responseBody = responseBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
