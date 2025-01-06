#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.BareBitcoin;

public class BareBitcoinLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public BareBitcoinLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
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

      /*  if (!kv.TryGetValue("server", out var server))
        {
            server = network.Name switch
            {
                nameof(Network.TestNet) => "https://api.staging.galoy.io/graphql",
                nameof(Network.RegTest) => "http://localhost:4455/graphql",
                _ => "https://api.blink.sv/graphql"
            };
            // error = $"The key 'server' is mandatory for blink connection strings";
            // return null;
        } */

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

        error = null;

        var client = _httpClientFactory.CreateClient();

        client.BaseAddress = uri;

        var walletId = "default"; //TODO: Delete

        var bclient = new BareBitcoinLightningClient(privateKey, publicKey, accountId, uri, walletId, network, client, _loggerFactory.CreateLogger($"{nameof(BareBitcoinLightningClient)}:{walletId}"));
      

        try
            {
                bclient.GetBalanceBareBitcoin().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error = "GetBalanceBareBitcoin failed";
                return null;
            }
      
      
      
       /* (Network Network, string DefaultWalletId, string DefaultWalletCurrency) res;
        try
        {
            res = bclient.GetBalanceBareBitcoin().GetAwaiter().GetResult();
            //res = bclient.GetNetworkAndDefaultWallet().GetAwaiter().GetResult();
            if (res.Network != network)
            {
                error = $"The wallet is not on the right network ({res.Network.Name} instead of {network.Name})";
                return null;
            }

            if (walletId is null && string.IsNullOrEmpty(res.DefaultWalletId))
            {
                error = $"The wallet-id is not set and no default wallet is set";
                return null;
            }
        }
        catch (Exception e)
        {
            error = $"Invalid server or api key";
            return null;
        }

        if (walletId is null)
        {
            bclient.WalletId = res.DefaultWalletId;
            bclient.WalletCurrency = res.DefaultWalletCurrency;
            bclient.Logger = _loggerFactory.CreateLogger($"{nameof(BareBitcoinLightningClient)}:{walletId}");
        }
        else
        {
            try
            {
                bclient.GetBalance().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error = "Invalid wallet id";
                return null;
            }
        }*/

        return bclient;
    }
}