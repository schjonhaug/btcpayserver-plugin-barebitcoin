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

        

        var bclient = new BareBitcoinLightningClient(privateKey, publicKey, accountId, uri, network, client, _loggerFactory.CreateLogger($"{nameof(BareBitcoinLightningClient)}"));
      

        try
            {
                bclient.GetBalance().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error = "GetBalance failed";
                return null;
            }
      
      
       

        return bclient;
    }
}