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

   

    public string? WalletCurrency { get; set; }

    private readonly Network _network;
    public ILogger Logger;

    // Static nonce tracking with async-compatible lock
    private static readonly SemaphoreSlim _nonceLock = new SemaphoreSlim(1, 1);
    private static long _lastNonce;

    // Balance caching
    private static readonly SemaphoreSlim _balanceLock = new SemaphoreSlim(1, 1);
    private LightningNodeBalance? _cachedBalance;
    private DateTime _lastBalanceCheck = DateTime.MinValue;
    private static readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(5);

    private async Task<long> GetNextNonce()
    {
        await _nonceLock.WaitAsync();
        try
        {
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _lastNonce = Math.Max(_lastNonce + 1, currentTimestamp);
            return _lastNonce;
        }
        finally
        {
            _nonceLock.Release();
        }
    }

    public BareBitcoinLightningClient(string privateKey, string publicKey, string accountId, Uri apiEndpoint, Network network, HttpClient httpClient, ILogger logger)
    {
      
        _privateKey = privateKey;
        _publicKey = publicKey;
        _accountId = accountId;

        _apiEndpoint = apiEndpoint;
        _network = network;
        Logger = logger;
        
        
        
    }

    public override string ToString()
    {
        return $"type=barebitcoin;server={_apiEndpoint};public-key={_publicKey};account-id={_accountId}";
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInvoice(invoiceId: {invoiceId})", invoiceId);
        try
        {
            var response = await MakeAuthenticatedRequest("GET", $"/v1/deposit-destinations/bitcoin/invoice/{invoiceId}");
            var responseObj = JObject.Parse(response);

            // Convert the API response to a LightningInvoice object
            var status = responseObj["status"]?.Value<string>() switch
            {
                "INVOICE_STATUS_UNPAID" => LightningInvoiceStatus.Unpaid,
                "INVOICE_STATUS_PAID" => LightningInvoiceStatus.Paid,
                "INVOICE_STATUS_EXPIRED" => LightningInvoiceStatus.Expired,
                "INVOICE_STATUS_CANCELED" => LightningInvoiceStatus.Expired,
                _ => LightningInvoiceStatus.Unpaid // Default case
            };

            var invoice = responseObj["invoice"]?.Value<string>();
            if (string.IsNullOrEmpty(invoice))
                return null;

            var bolt11 = BOLT11PaymentRequest.Parse(invoice, _network);
            
            return new LightningInvoice
            {
                Id = invoiceId,
                BOLT11 = invoice,
                Status = status,
                Amount = bolt11.MinimumAmount,
                ExpiresAt = bolt11.ExpiryDate,
                PaymentHash = bolt11.PaymentHash?.ToString() ?? string.Empty,
                PaidAt = status == LightningInvoiceStatus.Paid ? DateTimeOffset.UtcNow : null
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting invoice from BareBitcoin");
            return null;
        }
    }

    public LightningInvoice? ToInvoice(JObject invoice)
    {
        var paymentRequestToken = invoice["paymentRequest"];
        if (paymentRequestToken?.Value<string>() is not string paymentRequest)
            return null;
            
        var bolt11 = BOLT11PaymentRequest.Parse(paymentRequest, _network);
        return new LightningInvoice()
        {
            Id = invoice["paymentHash"]?.Value<string>() ?? string.Empty,
            Amount = invoice["satoshis"] is null ? bolt11.MinimumAmount : LightMoney.Satoshis(invoice["satoshis"]!.Value<long>()),
            Preimage = invoice["paymentSecret"]?.Value<string>(),
            PaidAt = (invoice["paymentStatus"]?.Value<string>()) == "PAID" ? DateTimeOffset.UtcNow : null,
            Status = (invoice["paymentStatus"]?.Value<string>()) switch
            {
                "EXPIRED" => LightningInvoiceStatus.Expired,
                "PAID" => LightningInvoiceStatus.Paid,
                "PENDING" => LightningInvoiceStatus.Unpaid,
                _ => LightningInvoiceStatus.Unpaid // Default case
            },
            BOLT11 = paymentRequest,
            PaymentHash = invoice["paymentHash"]?.Value<string>() ?? string.Empty,
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
        try
        {
            // Use the ledger endpoint to get all transactions
            var response = await MakeAuthenticatedRequest(
                "GET",
                _accountId != null ? $"/v1/ledger/{_accountId}" : "/v1/ledger"
            );

            var responseObj = JObject.Parse(response);
            var entries = responseObj["entries"] as JArray;

            if (entries == null)
                return Array.Empty<LightningInvoice>();

            var invoices = new List<LightningInvoice>();

            // Filter for deposit entries and get their details
            foreach (var entry in entries)
            {
                if (entry["type"]?.Value<string>() != "ENTRY_TYPE_DEPOSIT" || 
                    entry["currency"]?.Value<string>() != "CURRENCY_BTC")
                    continue;

                var transactionId = entry["transactionId"]?.Value<string>();
                if (string.IsNullOrEmpty(transactionId))
                    continue;

                // Get the invoice details
                var invoice = await GetInvoice(transactionId, cancellation);
                var isPendingOnly = request.PendingOnly.GetValueOrDefault(false);
                if (invoice != null && (!isPendingOnly || invoice.Status == LightningInvoiceStatus.Unpaid))
                {
                    invoices.Add(invoice);
                }
            }

            return invoices.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error listing invoices from BareBitcoin");
            throw;
        }
    }
    

    public async Task<LightningPayment?> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetPayment(paymentHash: {paymentHash})", paymentHash);
        return await Task.FromException<LightningPayment?>(new NotImplementedException());
    }

    public LightningPayment? ToLightningPayment(JObject transaction)
    {
        if ((transaction["direction"]?.Value<string>() ?? "") == "RECEIVE")
            return null;

        var initiationVia = transaction["initiationVia"];
        if (initiationVia?["paymentHash"] == null || initiationVia["paymentRequest"] == null)
            return null;

        var paymentRequest = initiationVia["paymentRequest"]!.Value<string>();
        if (paymentRequest == null)
            return null;
            
        var bolt11 = BOLT11PaymentRequest.Parse(paymentRequest, _network);
        var preimage = transaction["settlementVia"]?["preImage"]?.Value<string>();
        return new LightningPayment()
        {
            Amount = bolt11.MinimumAmount,
            Status = (transaction["status"]?.Value<string>() ?? "") switch
            {
                "FAILURE" => LightningPaymentStatus.Failed,
                "PENDING" => LightningPaymentStatus.Pending,
                "SUCCESS" => LightningPaymentStatus.Complete,
                _ => LightningPaymentStatus.Unknown
            },
            BOLT11 = paymentRequest,
            Id = initiationVia["paymentHash"]!.Value<string>() ?? string.Empty,
            PaymentHash = initiationVia["paymentHash"]!.Value<string>() ?? string.Empty,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(transaction["createdAt"]!.Value<long>()),
            AmountSent = bolt11.MinimumAmount,
            Preimage = preimage
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
        return await Task.FromException<LightningPayment[]>(new NotImplementedException());
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
        try
        {
            var requestData = new
            {
                accountId = _accountId,
                currency = "CURRENCY_BTC",
                amount = createInvoiceRequest.Amount.ToDecimal(LightMoneyUnit.BTC),
                publicDescription = createInvoiceRequest.Description,
                internalDescription = $"BTCPay Server Invoice - {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}"
            };

            var response = await MakeAuthenticatedRequest(
                "POST", 
                "/v1/deposit-destinations/bitcoin/invoice",
                JsonConvert.SerializeObject(requestData)
            );
            
            var responseObj = JObject.Parse(response);
            var depositDestinationId = responseObj["depositDestinationId"]?.Value<string>();
            var invoice = responseObj["invoice"]?.Value<string>();

            if (string.IsNullOrEmpty(invoice))
                throw new Exception("No invoice returned from BareBitcoin API");

            var bolt11 = BOLT11PaymentRequest.Parse(invoice, _network);

            return new LightningInvoice
            {
                Id = depositDestinationId ?? bolt11.PaymentHash?.ToString() ?? string.Empty,
                BOLT11 = invoice,
                Status = LightningInvoiceStatus.Unpaid,
                Amount = createInvoiceRequest.Amount,
                ExpiresAt = bolt11.ExpiryDate,
                PaymentHash = bolt11.PaymentHash?.ToString() ?? string.Empty,
                PaidAt = null
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating invoice with BareBitcoin");
            throw;
        }
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Listen()");
        var listener = new BareBitcoinListener(this, Logger);
        await Task.CompletedTask; // Add await to satisfy compiler
        return listener;
    }

    public class BareBitcoinListener : ILightningInvoiceListener
    {
        private readonly BareBitcoinLightningClient _lightningClient;
        private readonly Channel<LightningInvoice> _invoices = Channel.CreateUnbounded<LightningInvoice>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _pollingTask;
        private readonly ILogger _logger;
        private readonly HashSet<string> _processedInvoices = new HashSet<string>();

        public BareBitcoinListener(BareBitcoinLightningClient lightningClient, ILogger logger)
        {
            _lightningClient = lightningClient;
            _logger = logger;
            _pollingTask = StartPolling();
        }

        private async Task StartPolling()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Get all invoices
                    var invoices = await _lightningClient.ListInvoices(_cts.Token);
                    
                    // Check each invoice
                    foreach (var invoice in invoices)
                    {
                        // Skip if we've already processed this invoice
                        if (_processedInvoices.Contains(invoice.Id))
                            continue;

                        // Get fresh status
                        var currentInvoice = await _lightningClient.GetInvoice(invoice.Id, _cts.Token);
                        if (currentInvoice != null && currentInvoice.Status == LightningInvoiceStatus.Paid)
                        {
                            _logger.LogInformation("Invoice {InvoiceId} has been paid", invoice.Id);
                            _processedInvoices.Add(invoice.Id);
                            await _invoices.Writer.WriteAsync(currentInvoice, _cts.Token);
                        }
                    }

                    // Wait before next poll
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while polling for invoice updates");
                    // Wait before retry on error
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _pollingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore any errors during disposal
            }
            _cts.Dispose();
            _invoices.Writer.TryComplete();
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
                return await _invoices.Reader.ReadAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                throw new Exception("Listener has been disposed");
            }
        }
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInfo");
        return await Task.FromException<LightningNodeInformation>(new NotSupportedException());
    }



    

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(payParams: {payParams})", payParams);
        return await Task.FromException<PayResponse>(new NotImplementedException());
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11}, payParams: {payParams})", bolt11, payParams);
        return await Task.FromException<PayResponse>(new NotImplementedException());
    }
    

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11})", bolt11);
        return await Task.FromException<PayResponse>(new NotImplementedException());
    }


    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("CancelInvoice(invoiceId: {invoiceId})", invoiceId);
        await Task.FromException(new NotImplementedException());
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetDepositAddress()");
        return await Task.FromException<BitcoinAddress>(new NotImplementedException());
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("OpenChannel(request: {request})", openChannelRequest);
        return await Task.FromException<OpenChannelResponse>(new NotImplementedException());
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ConnectTo(nodeInfo: {nodeInfo})", nodeInfo);
        return await Task.FromException<ConnectionResult>(new NotImplementedException());
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListChannels()");
        return await Task.FromException<LightningChannel[]>(new NotImplementedException());
    }




    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new())
    {
        try 
        {
            await _balanceLock.WaitAsync(cancellation);
            try
            {
                var now = DateTime.UtcNow;
                if (_cachedBalance != null && (now - _lastBalanceCheck) < _cacheTimeout)
                {
                    Logger.LogInformation("Returning cached balance from {LastCheck}", _lastBalanceCheck);
                    return _cachedBalance;
                }

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
                
                _cachedBalance = new LightningNodeBalance()
                {
                    OffchainBalance = new OffchainBalance()
                    {
                        Local = LightMoney.Satoshis(satoshis)
                    }
                };
                _lastBalanceCheck = now;
                
                return _cachedBalance;
            }
            finally
            {
                _balanceLock.Release();
            }
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
            // Convert millisecond nonce to string
            var nonceStr = nonce.ToString();
            // Encode data: nonce and raw data
            var encodedData = data != null ? $"{nonceStr}{data}" : nonceStr;

            // SHA-256 hash of the encoded data
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashedData = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(encodedData));

            // Concatenate method, path, and hashed data
            var message = new List<byte>();
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(method));
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(path));
            message.AddRange(hashedData);

            // Decode secret from base64
            var decodedSecret = Convert.FromBase64String(secret);

            // Generate HMAC
            using var hmac = new System.Security.Cryptography.HMACSHA256(decodedSecret);
            var macsum = hmac.ComputeHash(message.ToArray());
            return Convert.ToBase64String(macsum);
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
            var nonce = await GetNextNonce();
            Logger.LogInformation("Making {method} request to {path} with nonce {nonce}", method, path, nonce);

            var hmac = CreateHmac(_privateKey, method, path, nonce, data);
            
            var requestUrl = $"https://api.bb.no{path}";
            var request = new HttpRequestMessage(new HttpMethod(method), requestUrl);
            request.Headers.Add("x-bb-api-hmac", hmac);
            request.Headers.Add("x-bb-api-key", _publicKey);
            request.Headers.Add("x-bb-api-nonce", nonce.ToString());

            if (data != null && method != "GET")
            {
                request.Content = new StringContent(data, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Request failed with status {StatusCode}. Response body: {Body}", 
                    response.StatusCode, responseContent);
                response.EnsureSuccessStatusCode();
            }
            
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
