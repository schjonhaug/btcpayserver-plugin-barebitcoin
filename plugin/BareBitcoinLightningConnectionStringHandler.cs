#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.BareBitcoin;

public class BareBitcoinLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBareBitcoinInvoiceService _invoiceService;
    private readonly IBareBitcoinStoreBinding _storeBinding;

    public BareBitcoinLightningConnectionStringHandler(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IBareBitcoinInvoiceService invoiceService,
        IBareBitcoinStoreBinding storeBinding)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _invoiceService = invoiceService;
        _storeBinding = storeBinding;
    }


    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "barebitcoin")
        {
            error = null;
            return null;
        }

        var server = "https://api.bb.no";
 

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) )
        {
            error = "Invalid server URL";
            return null;
        }

        bool allowInsecure = false;
        

        if (!LightningConnectionStringHelper.VerifySecureEndpoint(uri, allowInsecure))
        {
            error = "The key 'allowinsecure' is false, but server's Uri is not using https";
            return null;
        }
        if (!kv.TryGetValue("public-key", out var publicKey))
        {
            error = "The key 'public-key' is not found";
            return null;
        }

        if (!kv.TryGetValue("private-key", out var privateKey)) 
        {
            error = "The key 'private-key' is not found";
            return null;
        }

        if (!kv.TryGetValue("account-id", out var accountId))
        {
            error = "The key 'account-id' is not found";
            return null;
        }

        if (string.IsNullOrWhiteSpace(accountId))
        {
            error = "The key 'account-id' must not be empty";
            return null;
        }

        if (!kv.TryGetValue("store-id", out var storeId))
        {
            error = "The key 'store-id' is not found. Open this store's Lightning setup and save the connection again";
            return null;
        }

        if (string.IsNullOrWhiteSpace(storeId))
        {
            error = "The key 'store-id' must not be empty";
            return null;
        }

        if (!kv.TryGetValue("store-binding", out var protectedStoreId) ||
            !_storeBinding.IsValid(storeId, protectedStoreId))
        {
            error = "The key 'store-binding' is missing or does not authenticate this BTCPay store. Open this store's Lightning setup and save the connection again";
            return null;
        }

        var maxPollConcurrency = 10;
        if (kv.TryGetValue("max-poll-concurrency", out var maxPollConcurrencyStr))
        {
            if (!int.TryParse(maxPollConcurrencyStr, out maxPollConcurrency) || maxPollConcurrency is < 1 or > 100)
            {
                error = "The key 'max-poll-concurrency' must be an integer between 1 and 100";
                return null;
            }
        }

        error = null;

        var client = _httpClientFactory.CreateClient();

        client.BaseAddress = uri;

        

        var bclient = new BareBitcoinLightningClient(privateKey, publicKey, accountId, storeId, uri, network, client, _loggerFactory.CreateLogger($"{nameof(BareBitcoinLightningClient)}"), _invoiceService, maxPollConcurrency);
      

        try
            {
                bclient.GetBalance().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _loggerFactory.CreateLogger(nameof(BareBitcoinLightningConnectionStringHandler)).LogError(e, "Failed to parse BareBitcoin connection string");
                return null;
            }
      
      
       

        return bclient;
    }
}
