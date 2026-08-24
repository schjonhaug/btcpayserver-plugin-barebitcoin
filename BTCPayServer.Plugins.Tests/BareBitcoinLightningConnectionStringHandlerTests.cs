using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinLightningConnectionStringHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public BareBitcoinLightningConnectionStringHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private const string BaseConnectionString =
        "type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123;store-id=store-123;store-binding=signed-store-123";

    private BareBitcoinLightningConnectionStringHandler CreateHandler()
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
        var invoiceService = new BareBitcoinInvoiceService(NullLogger.Instance, Path.Combine(_tempDir, "tracked-invoices.json"));
        return new BareBitcoinLightningConnectionStringHandler(
            httpClientFactory,
            NullLoggerFactory.Instance,
            invoiceService,
            new StubStoreBinding());
    }

    [Fact]
    public void Create_ReturnsClientWithoutHttpContextDependency()
    {
        var handler = CreateHandler();

        var client = handler.Create(BaseConnectionString, Network.Main, out var error);

        Assert.NotNull(client);
        Assert.Null(error);
    }

    [Fact]
    public void Create_WithMaxPollConcurrency_ReturnsClient()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            BaseConnectionString + ";max-poll-concurrency=5",
            Network.Main,
            out var error);

        Assert.NotNull(client);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Create_WithBoundaryMaxPollConcurrency_ReturnsClient(int concurrency)
    {
        var handler = CreateHandler();

        var client = handler.Create(
            BaseConnectionString + $";max-poll-concurrency={concurrency}",
            Network.Main,
            out var error);

        Assert.NotNull(client);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("abc")]
    [InlineData("")]
    public void Create_WithInvalidMaxPollConcurrency_ReturnsError(string value)
    {
        var handler = CreateHandler();

        var client = handler.Create(
            BaseConnectionString + $";max-poll-concurrency={value}",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Equal("The key 'max-poll-concurrency' must be an integer between 1 and 100", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Create_WithBlankAccountId_ReturnsError(string accountId)
    {
        var handler = CreateHandler();

        var client = handler.Create(
            $"type=barebitcoin;public-key=public-key;private-key=private-key;account-id={accountId}",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Equal("The key 'account-id' must not be empty", error);
    }

    [Fact]
    public void Create_WithoutStoreId_ReturnsIsolationError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Contains("store-id", error);
        Assert.Contains("Lightning setup", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Create_WithBlankStoreId_ReturnsError(string storeId)
    {
        var handler = CreateHandler();

        var client = handler.Create(
            $"type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123;store-id={storeId}",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Equal("The key 'store-id' must not be empty", error);
    }

    [Fact]
    public void Create_WithoutStoreBinding_ReturnsAuthenticationError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123;store-id=store-123",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Contains("store-binding", error);
        Assert.Contains("authenticate", error);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("signed-other-store")]
    public void Create_WithInvalidOrMismatchedStoreBinding_ReturnsAuthenticationError(string storeBinding)
    {
        var handler = CreateHandler();

        var client = handler.Create(
            $"type=barebitcoin;public-key=public-key;private-key=private-key;account-id=account-123;store-id=store-123;store-binding={storeBinding}",
            Network.Main,
            out var error);

        Assert.Null(client);
        Assert.Contains("store-binding", error);
        Assert.Contains("authenticate", error);
    }

    [Fact]
    public void StoreBinding_AuthenticatesOnlyTheProtectedStore()
    {
        var binding = new BareBitcoinStoreBinding(new EphemeralDataProtectionProvider());
        var protectedStore = binding.Protect("store-a");

        Assert.True(binding.IsValid("store-a", protectedStore));
        Assert.True(binding.IsValid(" store-a ", protectedStore));
        Assert.False(binding.IsValid("store-b", protectedStore));
        Assert.False(binding.IsValid("store-a", protectedStore + "tampered"));
    }

    private sealed class StubStoreBinding : IBareBitcoinStoreBinding
    {
        public string Protect(string storeId) => $"signed-{storeId.Trim()}";

        public bool IsValid(string storeId, string protectedStoreId) =>
            StringComparer.Ordinal.Equals(Protect(storeId), protectedStoreId);
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
