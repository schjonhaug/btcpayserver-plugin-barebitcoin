#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using GraphQL;
using GraphQL.Client.Abstractions.Websocket;
using GraphQL.Client.Http;
using GraphQL.Client.Http.Websocket;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.BareBitcoin;

public class BareBitcoinLightningClient : ILightningClient
{

 
    private readonly string _privateKey;
    private readonly string _publicKey;
    private readonly string _accountId; 
    private readonly Uri _apiEndpoint;
    public  string? WalletId { get; set; } // TODO: Delete
    private readonly string _apiKey; // TODO Delete
   

    public string? WalletCurrency { get; set; }

    private readonly Network _network;
    public ILogger Logger;
    private readonly GraphQLHttpClient _client;

    public class BlinkConnectionInit
    {
        [JsonProperty("X-API-KEY")] public string ApiKey { get; set; }
    }
    public BareBitcoinLightningClient(string privateKey, string publicKey, string accountId, Uri apiEndpoint, string walletId, Network network, HttpClient httpClient, ILogger logger)
    {
        _apiKey = "TODO-DELETE";
        var apiKey = "TODO-DELETE";
        _privateKey = privateKey;
        _publicKey = publicKey;
        _accountId = accountId;

        _apiEndpoint = apiEndpoint;
        WalletId = walletId;
        _network = network;
        Logger = logger;
        _client = new GraphQLHttpClient(new GraphQLHttpClientOptions() {EndPoint = _apiEndpoint,
            WebSocketEndPoint =
                new Uri("wss://" + _apiEndpoint.Host.Replace("api.", "ws.") + _apiEndpoint.PathAndQuery),
            WebSocketProtocol = WebSocketProtocols.GRAPHQL_TRANSPORT_WS,
            ConfigureWebSocketConnectionInitPayload = options => new BlinkConnectionInit() {ApiKey = apiKey},
            ConfigureWebsocketOptions =
                _ => { }
        }, new NewtonsoftJsonSerializer(settings =>
        {
            if (settings.ContractResolver is CamelCasePropertyNamesContractResolver
                camelCasePropertyNamesContractResolver)
            {
                camelCasePropertyNamesContractResolver.NamingStrategy.OverrideSpecifiedNames = false;
                camelCasePropertyNamesContractResolver.NamingStrategy.ProcessDictionaryKeys = false;
            }
        }), httpClient);
        
        
    }

    public override string ToString()
    {
        return $"type=blink;server={_apiEndpoint};api-key={_apiKey}{(WalletId is null? "":$";wallet-id={WalletId}")}";
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInvoice(invoiceId: {invoiceId})", invoiceId);
        var reques = new GraphQLRequest
        {
            Query = @"
query InvoiceByPaymentHash($paymentHash: PaymentHash!, $walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        invoiceByPaymentHash(paymentHash: $paymentHash) {
          createdAt
          paymentHash
          paymentRequest
          paymentSecret
          paymentStatus
        }
      }
    }
  }
}",
            OperationName = "InvoiceByPaymentHash",
            Variables = new
            {
                walletId = WalletId,
                paymentHash = invoiceId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        

        return response.Data is null ? null : ToInvoice(response.Data.me.defaultAccount.walletById.invoiceByPaymentHash);
    }

    public LightningInvoice? ToInvoice(JObject invoice)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(invoice["paymentRequest"].Value<string>(), _network);
        return new LightningInvoice()
        {
            Id = invoice["paymentHash"].Value<string>(),
            Amount = invoice["satoshis"] is null? bolt11.MinimumAmount:  LightMoney.Satoshis(invoice["satoshis"].Value<long>()),
                Preimage =  invoice["paymentSecret"].Value<string>(),
            PaidAt = (invoice["paymentStatus"].Value<string>()) ==  "PAID"? DateTimeOffset.UtcNow : null,
            Status =  (invoice["paymentStatus"].Value<string>()) switch
            {
                "EXPIRED" => LightningInvoiceStatus.Expired,
                "PAID" => LightningInvoiceStatus.Paid,
                "PENDING" => LightningInvoiceStatus.Unpaid
            },
            BOLT11 =  invoice["paymentRequest"].Value<string>(),
            PaymentHash = invoice["paymentHash"].Value<string>(),
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInvoice(paymentHash: {paymentHash})", paymentHash);
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListInvoices()");
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListInvoices(request: {request})", request);
        var reques = new GraphQLRequest
        {
            Query = @"
query Invoices($walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        invoices {
          edges {
            node {
              createdAt
              paymentHash
              paymentRequest
              paymentSecret
              paymentStatus
              ... on LnInvoice {
                satoshis
              }
            }
          }
        }
      }
    }
  }
}",
            OperationName = "Invoices",
            Variables = new
            {
                walletId = WalletId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);

        var result = ((JArray)response.Data.me.defaultAccount.walletById.invoices.edges).Select(o => ToInvoice((JObject) o["node"] )).Where(o => o is not null || request.PendingOnly is not true || o.Status == LightningInvoiceStatus.Unpaid).ToArray();
        return (LightningInvoice[]) result;
    }

    public async Task<LightningPayment?> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetPayment(paymentHash: {paymentHash})", paymentHash);
        var reques = new GraphQLRequest
        {
            Query = @"
query TransactionsByPaymentHash($paymentHash: PaymentHash!, $walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        transactionsByPaymentHash(paymentHash: $paymentHash) {
          createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
            ... on SettlementViaIntraLedger {
              preImage
            }
          }
          status
        }
      }
    }
  }
}",
            OperationName = "TransactionsByPaymentHash",
            Variables = new
            {
                walletId = WalletId,
                paymentHash = paymentHash
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        var item = (JArray) response.Data.me.defaultAccount.walletById.transactionsByPaymentHash;
        return item.Any()? ToLightningPayment((JObject)item.First()): null;
    }

    public LightningPayment? ToLightningPayment(JObject transaction)
    {
        if ((string)transaction["direction"] == "RECEIVE")
            return null;

        var initiationVia = transaction["initiationVia"];
        if (initiationVia?["paymentHash"] == null)
            return null;

        var bolt11 = BOLT11PaymentRequest.Parse((string)initiationVia["paymentRequest"], _network);
        var preimage = transaction["settlementVia"]?["preImage"]?.Value<string>();
        return new LightningPayment()
        {
            Amount = bolt11.MinimumAmount,
            Status = transaction["status"].ToString() switch
            {
                "FAILURE" => LightningPaymentStatus.Failed,
                "PENDING" => LightningPaymentStatus.Pending,
                "SUCCESS" => LightningPaymentStatus.Complete,
                _ => LightningPaymentStatus.Unknown
            },
            BOLT11 = (string)initiationVia["paymentRequest"],
            Id = (string)initiationVia["paymentHash"],
            PaymentHash = (string)initiationVia["paymentHash"],
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(transaction["createdAt"].Value<long>()),
            AmountSent = bolt11.MinimumAmount,
            Preimage =  preimage

        };
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListPayments()");
        return await ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListPayments(request: {request})", request);
        var reques = new GraphQLRequest
        {
            Query = @"
query Transactions($walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        transactions {
          edges {
            node {
          createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
            ... on SettlementViaIntraLedger {
              preImage
            }
          }
          status
            }
          }
        }
      }
    }
  }
}",
            OperationName = "Transactions",
            Variables = new
            {
                walletId = WalletId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        
            
            
        var result = ((JArray)response.Data.me.defaultAccount.walletById.transactions.edges).Select(o => ToLightningPayment((JObject) o["node"])).Where(o => o is not null && (request.IncludePending is not true || o.Status!= LightningPaymentStatus.Pending)).ToArray();
        return (LightningPayment[]) result;
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new())
    {
        Logger.LogInformation("CreateInvoice(amount: {amount}, description: {description}, expiry: {expiry})", 
            amount, description, expiry);
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new())
    {
        Logger.LogInformation("CreateInvoice(request: {request})", createInvoiceRequest);
        string query;
        
        query = WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true ? @"
mutation lnInvoiceCreate($input: LnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipientInput!) {
  lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient(input: $input) {
    invoice {
      createdAt
      paymentHash
      paymentRequest
      paymentSecret
      paymentStatus
      satoshis
    },
    errors{
      message
    }
  }
}" : @"
mutation lnInvoiceCreate($input: LnInvoiceCreateOnBehalfOfRecipientInput!) {
  lnInvoiceCreateOnBehalfOfRecipient(input: $input) {
    invoice {
      createdAt
      paymentHash
      paymentRequest
      paymentSecret
      paymentStatus
      satoshis
    },
    errors{
      message
    }
  }
}";
        
        var reques = new GraphQLRequest
        {
            Query = query,
            OperationName = "lnInvoiceCreate",
            Variables = new
            {
                input = new
                {
                    recipientWalletId = WalletId,
                    memo = createInvoiceRequest.Description,
                    descriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
                    amount = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi),
expiresIn = (int)createInvoiceRequest.Expiry.TotalMinutes
                    
                }
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        var inv = (WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true
            ? response.Data.lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient.invoice
            : response.Data.lnInvoiceCreateOnBehalfOfRecipient.invoice)as JObject;

        if (inv is null)
        {
            var errors = (WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true
                ? response.Data.lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient.errors
                : response.Data.lnInvoiceCreateOnBehalfOfRecipient.errors) as JArray;

            if (errors.Any())
            {
                throw new Exception(errors.First()["message"].ToString());
            }
        }
        return ToInvoice(inv);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Listen()");
        return new BlinkListener(_client, this, Logger);
    }

    public class BlinkListener : ILightningInvoiceListener
    {
        private readonly BareBitcoinLightningClient _lightningClient;
        private readonly Channel<LightningInvoice> _invoices = Channel.CreateUnbounded<LightningInvoice>();
        private readonly IDisposable _subscription;

        public BlinkListener(GraphQLHttpClient httpClient, BareBitcoinLightningClient lightningClient, ILogger logger)
        {
            try
            {

                _lightningClient = lightningClient;
                var stream = httpClient.CreateSubscriptionStream<JObject>(new GraphQLRequest()
                {
                    Query = @"subscription myUpdates {
  myUpdates {
    update {
      ... on LnUpdate {
        transaction {
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
            }
          }
          direction
        }
      }
    }
  }
}
", OperationName = "myUpdates"
                });

                _subscription = stream.Subscribe(async response =>
                {
                    try
                    {
                        if(response.Data is null)
                            return;
                        if (response.Data.SelectToken("myUpdates.update.transaction.direction")?.Value<string>() != "RECEIVE")
                            return;
                        var invoiceId = response.Data
                            .SelectToken("myUpdates.update.transaction.initiationVia.paymentHash")?.Value<string>();
                        if (invoiceId is null)
                            return;
                        if (await _lightningClient.GetInvoice(invoiceId) is LightningInvoice inv)
                        {
                            _invoices.Writer.TryWrite(inv);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error while processing detecting lightning invoice payment");
                    }
                   
                });
                _wsSubscriptionDisposable = httpClient.WebsocketConnectionState.Subscribe(state =>
                {
                    if (state == GraphQLWebsocketConnectionState.Disconnected)
                    {
                        streamEnded.TrySetResult();
                    }
                });
                
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while creating lightning invoice listener");
            }
        }
        public void Dispose()
        {
            _subscription.Dispose();
            _invoices.Writer.TryComplete();
            _wsSubscriptionDisposable.Dispose();
            streamEnded.TrySetResult();
        }

        private TaskCompletionSource streamEnded = new();
        private readonly IDisposable _wsSubscriptionDisposable;

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            var resultz = await Task.WhenAny(streamEnded.Task, _invoices.Reader.ReadAsync(cancellation).AsTask());
            if (resultz is Task<LightningInvoice> res)
            {
                return await res;
            }

            throw new Exception("Stream disconnected, cannot await invoice");
        }
    }
    public async Task<(Network Network, string DefaultWalletId, string DefaultWalletCurrency)> GetNetworkAndDefaultWallet(CancellationToken cancellation =default)
    {
        Logger.LogInformation("GetNetworkAndDefaultWallet()");
        var reques = new GraphQLRequest
        {
            Query = @"
query GetNetworkAndDefaultWallet {
  globals {
    network
  }
  me {
    defaultAccount {
      defaultWallet{
        id
        currency
      }
    }
  }
}",
            OperationName = "GetNetworkAndDefaultWallet"
        };
        
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);

        var defaultWalletId = (string) response.Data.me.defaultAccount.defaultWallet.id;
        var defaultWalletCurrency = (string) response.Data.me.defaultAccount.defaultWallet.currency;
        var network = response.Data.globals.network.ToString() switch
        {
            "mainnet" => Network.Main,
            "testnet" => Network.TestNet,
            "signet" => Network.TestNet,
            "regtest" => Network.RegTest,
            _ => throw new ArgumentOutOfRangeException()
        };
        return (network, defaultWalletId, defaultWalletCurrency);
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInfo");

        throw new NotSupportedException();
    }



    

    public async Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(payParams: {payParams})", payParams);
        return await Pay(null, new PayInvoiceParams(), cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11}, payParams: {payParams})", bolt11, payParams);
        var request = new GraphQLRequest
        {
            Query = @"
mutation LnInvoicePaymentSend($input: LnInvoicePaymentInput!) {
  lnInvoicePaymentSend(input: $input) {
    transaction {
      createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
            ... on SettlementViaIntraLedger {
              preImage
            }
          }
          status
    }
    errors {
      message
    }
    status
  }
}",
            OperationName = "LnInvoicePaymentSend",
            Variables = new {
                input = new {
                    walletId = WalletId,
                    paymentRequest = bolt11,
                }
            }
        };
        var bolt11Parsed = BOLT11PaymentRequest.Parse(bolt11, _network);
       
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
            new CancellationTokenSource(payParams?.SendTimeout ?? PayInvoiceParams.DefaultSendTimeout).Token);
        var response =(JObject) (await  _client.SendQueryAsync<dynamic>(request,  cts.Token)).Data.lnInvoicePaymentSend;
        
        var result = new PayResponse();
        result.Result = response["status"].Value<string>() switch
        {
            "ALREADY_PAID" => PayResult.Ok,
            "FAILURE" => PayResult.Error,
            "PENDING"=> PayResult.Unknown,
            "SUCCESS" => PayResult.Ok,
            null => PayResult.Unknown,
            _ => throw new ArgumentOutOfRangeException()
        };
        if (result.Result == PayResult.Error && response.TryGetValue("errors", out var error))
        {
            if (error.ToString().Contains("ResourceAttemptsRedlockServiceError", StringComparison.InvariantCultureIgnoreCase))
            {
                await Task.Delay(Random.Shared.Next(200, 600), cts.Token);
                return await Pay(bolt11, payParams, cts.Token);
            }
            if (error is JArray { Count: > 0 } arr)
                result.ErrorDetail = arr[0]["message"]?.Value<string>();
        }
        if (response["transaction"]?.Value<JObject>() is not null)
        {
            result.Details = new PayDetails()
            {
                PaymentHash = bolt11Parsed.PaymentHash ?? new uint256(response["transaction"]["initiationVia"]["paymentHash"].Value<string>()),
                Status = response["status"].Value<string>() switch
                {
                    "ALREADY_PAID"  => LightningPaymentStatus.Complete,
                    "FAILURE" => LightningPaymentStatus.Failed,
                    "PENDING" => LightningPaymentStatus.Pending,
                    "SUCCESS" => LightningPaymentStatus.Complete,
                    null => LightningPaymentStatus.Unknown,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Preimage = response["transaction"]["settlementVia"]?["preImage"].Value<string>() is null? null: new uint256(response["transaction"]["settlementVia"]["preImage"].Value<string>()),
            };
        }

        return result;
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11})", bolt11);
        return await Pay(bolt11, new PayInvoiceParams(), cancellation);
    }


    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("CancelInvoice(invoiceId: {invoiceId})", invoiceId);
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetDepositAddress()");
        throw new NotImplementedException();
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("OpenChannel(request: {request})", openChannelRequest);
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ConnectTo(nodeInfo: {nodeInfo})", nodeInfo);
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListChannels()");
        throw new NotImplementedException();
    }




    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new())
    {
        try 
        {
            Logger.LogInformation("Getting balance from BareBitcoin");
            var response = await MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts");
            Logger.LogInformation("Received balance response: {response}", response);
            
            // Parse response according to OpenAPI spec
            var accounts = JObject.Parse(response)["accounts"] as JArray;
            if (accounts == null || !accounts.Any())
            {
                Logger.LogWarning("No bitcoin accounts found in response");
                return new LightningNodeBalance();
            }

            // If we have an accountId, find that specific account
            var account = _accountId != null 
                ? accounts.FirstOrDefault(a => a["id"]?.ToString() == _accountId)
                : accounts.First();

            if (account == null)
            {
                Logger.LogWarning("Account {AccountId} not found in response", _accountId);
                return new LightningNodeBalance();
            }

            // Get availableBtc and convert to satoshis (1 BTC = 100,000,000 sats)
            var availableBtc = account["availableBtc"]?.Value<double>() ?? 0;
            var satoshis = (long)(availableBtc * 100_000_000);

            Logger.LogInformation("Creating LightningNodeBalance response with {AvailableBtc} BTC ({Satoshis} sats)", availableBtc, satoshis);
            
            return new LightningNodeBalance()
            {
                OffchainBalance = new OffchainBalance()
                {
                    Local = LightMoney.Satoshis(satoshis)
                }
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting balance from BareBitcoin");
            throw;
        }
    }

    private string CreateHmac(string secret, string method, string path, long nonce, string? data = null)
    {
        try 
        {
            // Encode data: nonce and raw data
            var encodedData = data != null ? $"{nonce}{data}" : nonce.ToString();
            Logger.LogInformation("Creating HMAC with encodedData: {encodedData}", encodedData);

            // SHA-256 hash of the encoded data
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashedData = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(encodedData));
            Logger.LogInformation("Hashed data (hex): {hashedData}", BitConverter.ToString(hashedData).Replace("-", ""));

            // Concatenate method, path, and hashed data
            var message = new List<byte>();
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(method));
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(path));
            message.AddRange(hashedData);
            Logger.LogInformation("Combined message (hex): {message}", BitConverter.ToString(message.ToArray()).Replace("-", ""));

            // Decode secret from base64
            var decodedSecret = Convert.FromBase64String(secret);
            Logger.LogInformation("Decoded secret length: {length} bytes", decodedSecret.Length);

            // Generate HMAC
            using var hmac = new System.Security.Cryptography.HMACSHA256(decodedSecret);
            var macsum = hmac.ComputeHash(message.ToArray());
            var result = Convert.ToBase64String(macsum);
            
            Logger.LogInformation("Generated HMAC: {hmac}", result);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating HMAC signature");
            throw;
        }
    }

    private async Task<string> MakeAuthenticatedRequest(string method, string path, string? data = null)
    {
        try 
        {
            using var httpClient = new HttpClient();
            var nonce = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Logger.LogInformation("Making {method} request to {path} with nonce {nonce}", method, path, nonce);
            
            if (data != null)
            {
                Logger.LogInformation("Request data: {data}", data);
            }

            var hmac = CreateHmac(_privateKey, method, path, nonce, data);
            
            var requestUrl = $"https://api.bb.no{path}";
            Logger.LogInformation("Full request URL: {url}", requestUrl);
            
            var request = new HttpRequestMessage(new HttpMethod(method), requestUrl);
            request.Headers.Add("x-bb-api-hmac", hmac);
            request.Headers.Add("x-bb-api-key", _publicKey);
            request.Headers.Add("x-bb-api-nonce", nonce.ToString());

            Logger.LogInformation("Request headers:");
            Logger.LogInformation("x-bb-api-hmac: {hmac}", hmac);
            Logger.LogInformation("x-bb-api-key: {key}", _publicKey);
            Logger.LogInformation("x-bb-api-nonce: {nonce}", nonce);

            if (data != null && method != "GET")
            {
                request.Content = new StringContent(data, System.Text.Encoding.UTF8, "application/json");
                Logger.LogInformation("Added request content-type: application/json");
            }

            var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            Logger.LogInformation("Response status: {statusCode}", response.StatusCode);
            Logger.LogInformation("Response content: {content}", responseContent);
            
            response.EnsureSuccessStatusCode();
            return responseContent;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP request failed");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error making authenticated request");
            throw;
        }
    }

}