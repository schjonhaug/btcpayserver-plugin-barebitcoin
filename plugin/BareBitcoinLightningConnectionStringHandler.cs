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
    private readonly BareBitcoinInvoiceService _invoiceService;

    public BareBitcoinLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, BareBitcoinInvoiceService invoiceService)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _invoiceService = invoiceService;
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

        

        var bclient = new BareBitcoinLightningClient(privateKey, publicKey, accountId, uri, network, client, _loggerFactory.CreateLogger($"{nameof(BareBitcoinLightningClient)}"), _invoiceService, maxPollConcurrency);
      

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
