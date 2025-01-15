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
    private readonly HttpClient _httpClient;

   

    public string? WalletCurrency { get; set; }

    private readonly Network _network;
    public ILogger Logger;

    // Static nonce tracking with async-compatible lock
    private static readonly SemaphoreSlim _nonceLock = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
    private static long _lastNonce;

    // Balance caching
    private static readonly SemaphoreSlim _balanceLock = new SemaphoreSlim(1, 1);
    private LightningNodeBalance? _cachedBalance;
    private DateTime _lastBalanceCheck = DateTime.MinValue;
    private static readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _knownInvoiceIds = new HashSet<string>();
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private ILightningInvoiceListener? _currentListener;
    private readonly SemaphoreSlim _listenerLock = new SemaphoreSlim(1, 1);

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
        _httpClient = httpClient;
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

            var invoice = responseObj["invoice"]?.Value<string>();
            if (string.IsNullOrEmpty(invoice))
            {
                Logger.LogWarning("Invoice {InvoiceId} not found or empty response", invoiceId);
                return null;
            }

            var bolt11 = BOLT11PaymentRequest.Parse(invoice, _network);
            var amount = bolt11.MinimumAmount;
            
            // Convert the API response to a LightningInvoice object
            var status = responseObj["status"]?.Value<string>() switch
            {
                "INVOICE_STATUS_UNPAID" => LightningInvoiceStatus.Unpaid,
                "INVOICE_STATUS_PAID" => LightningInvoiceStatus.Paid,
                "INVOICE_STATUS_EXPIRED" => LightningInvoiceStatus.Expired,
                "INVOICE_STATUS_CANCELED" => LightningInvoiceStatus.Expired,
                _ => LightningInvoiceStatus.Unpaid // Default case
            };

            var paidAt = status == LightningInvoiceStatus.Paid ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            var preimage = status == LightningInvoiceStatus.Paid ? (responseObj["preimage"]?.Value<string>() ?? "00") : null;
            var amountReceived = status == LightningInvoiceStatus.Paid ? amount : null;
            
            // Only remove from tracking if paid or expired
            if (status == LightningInvoiceStatus.Expired || status == LightningInvoiceStatus.Paid)
            {
                await _invoiceTrackingLock.WaitAsync(cancellation);
                try
                {
                    if (_knownInvoiceIds.Contains(invoiceId))
                    {
                        Logger.LogInformation("Removing {Status} invoice {InvoiceId} from tracking list", status, invoiceId);
                        _knownInvoiceIds.Remove(invoiceId);
                    }
                }
                finally
                {
                    _invoiceTrackingLock.Release();
                }
            }

            var result = new LightningInvoice
            {
                Id = invoiceId,
                BOLT11 = invoice,
                Status = status,
                Amount = amount,
                AmountReceived = amountReceived,
                ExpiresAt = bolt11.ExpiryDate,
                PaymentHash = bolt11.PaymentHash?.ToString() ?? string.Empty,
                PaidAt = paidAt,
                Preimage = preimage
            };

            Logger.LogInformation("Returning invoice {InvoiceId} with status {Status}, AmountReceived: {AmountReceived}, Preimage: {Preimage}", 
                result.Id, result.Status, result.AmountReceived, result.Preimage);

            return result;
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
        var status = (invoice["paymentStatus"]?.Value<string>()) switch
        {
            "EXPIRED" => LightningInvoiceStatus.Expired,
            "PAID" => LightningInvoiceStatus.Paid,
            "PENDING" => LightningInvoiceStatus.Unpaid,
            _ => LightningInvoiceStatus.Unpaid // Default case
        };
        
        return new LightningInvoice()
        {
            Id = invoice["paymentHash"]?.Value<string>() ?? string.Empty,
            Amount = invoice["satoshis"] is null ? bolt11.MinimumAmount : LightMoney.Satoshis(invoice["satoshis"]!.Value<long>()),
            Preimage = invoice["paymentSecret"]?.Value<string>(),
            PaidAt = (status == LightningInvoiceStatus.Paid) ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            Status = status,
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
            await _invoiceTrackingLock.WaitAsync(cancellation);
            try
            {
                var invoices = new List<LightningInvoice>();
                var isPendingOnly = request.PendingOnly.GetValueOrDefault(false);

                foreach (var invoiceId in _knownInvoiceIds.ToList())
                {
                    var invoice = await GetInvoice(invoiceId, cancellation);
                    if (invoice != null)
                    {
                        if (!isPendingOnly || invoice.Status == LightningInvoiceStatus.Unpaid)
                        {
                            Logger.LogDebug("Adding invoice {InvoiceId} with status {Status} to results", 
                                invoice.Id, invoice.Status);
                            invoices.Add(invoice);
                        }
                    }
                }

                Logger.LogInformation("Found {Count} matching invoices (PendingOnly: {PendingOnly})", 
                    invoices.Count, isPendingOnly);

                return invoices.ToArray();
            }
            finally
            {
                _invoiceTrackingLock.Release();
            }
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
        await Task.CompletedTask;
        throw new NotImplementedException();
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
        await Task.CompletedTask;
        throw new NotImplementedException();
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
            // Start listener in background
            _ = Task.Run(async () => 
            {
                try 
                {
                    await Listen(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error starting listener");
                }
            });

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
            var invoiceId = depositDestinationId ?? bolt11.PaymentHash?.ToString() ?? string.Empty;

            // Add to tracking list
            await _invoiceTrackingLock.WaitAsync(cancellation);
            try
            {
                _knownInvoiceIds.Add(invoiceId);
                Logger.LogInformation("Added invoice {InvoiceId} to tracking list (now tracking {Count} invoices)", 
                    invoiceId, _knownInvoiceIds.Count);
            }
            finally
            {
                _invoiceTrackingLock.Release();
            }

            return new LightningInvoice
            {
                Id = invoiceId,
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
        await _listenerLock.WaitAsync(cancellation);
        try
        {
            if (_currentListener != null)
            {
                Logger.LogInformation("Returning existing listener");
                return _currentListener;
            }

            Logger.LogInformation("Creating new listener");
            var listener = new BareBitcoinListener(this, Logger);
            _currentListener = listener;
            return listener;
        }
        finally
        {
            _listenerLock.Release();
        }
    }

    public class BareBitcoinListener : ILightningInvoiceListener
    {
        private readonly BareBitcoinLightningClient _lightningClient;
        private readonly Channel<LightningInvoice> _invoices = Channel.CreateUnbounded<LightningInvoice>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _pollingTask;
        private readonly ILogger _logger;
        private readonly HashSet<string> _processedPayments = new HashSet<string>();
        private readonly HashSet<string> _watchedInvoices = new HashSet<string>();

        public BareBitcoinListener(BareBitcoinLightningClient lightningClient, ILogger logger)
        {
            _lightningClient = lightningClient;
            _logger = logger;
            _pollingTask = StartPolling();
        }

        private async Task StartPolling()
        {
            _logger.LogInformation("Starting invoice polling task");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Get the current list of invoices to watch
                    await _lightningClient._invoiceTrackingLock.WaitAsync(_cts.Token);
                    try
                    {
                        foreach (var invoiceId in _lightningClient._knownInvoiceIds)
                        {
                            if (!_watchedInvoices.Contains(invoiceId))
                            {
                                _logger.LogInformation("Adding invoice {InvoiceId} to watch list", invoiceId);
                                _watchedInvoices.Add(invoiceId);
                            }
                        }
                    }
                    finally
                    {
                        _lightningClient._invoiceTrackingLock.Release();
                    }

                    // Check each watched invoice
                    foreach (var invoiceId in _watchedInvoices.ToList())
                    {
                        _logger.LogDebug("Checking status of invoice {InvoiceId}", invoiceId);
                        var invoice = await _lightningClient.GetInvoice(invoiceId, _cts.Token);
                        
                        if (invoice == null)
                        {
                            _logger.LogWarning("Invoice {InvoiceId} no longer exists, removing from watch list", invoiceId);
                            _watchedInvoices.Remove(invoiceId);
                            continue;
                        }

                        if (invoice.Status == LightningInvoiceStatus.Paid)
                        {
                            if (!_processedPayments.Contains(invoice.Id))
                            {
                                _logger.LogInformation("Invoice {InvoiceId} has been paid, notifying BTCPay", invoice.Id);
                                _processedPayments.Add(invoice.Id);
                                
                                try 
                                {
                                    await _invoices.Writer.WriteAsync(invoice, _cts.Token);
                                    _logger.LogInformation("Successfully wrote payment notification to channel for invoice {InvoiceId}", invoice.Id);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to write payment notification to channel for invoice {InvoiceId}", invoice.Id);
                                }
                            }
                            _watchedInvoices.Remove(invoiceId);
                        }
                        else if (invoice.Status == LightningInvoiceStatus.Expired)
                        {
                            _logger.LogInformation("Invoice {InvoiceId} has expired, removing from watch list", invoiceId);
                            _watchedInvoices.Remove(invoiceId);
                        }
                    }

                    if (_watchedInvoices.Count > 0)
                    {
                        _logger.LogDebug("Currently watching {Count} invoices: {Invoices}", 
                            _watchedInvoices.Count,
                            string.Join(", ", _watchedInvoices));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while polling for invoice updates");
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
            }
        }

        public void Dispose()
        {
            _logger.LogInformation("Disposing listener");
            _cts.Cancel();
            try
            {
                _pollingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while waiting for polling task to complete during disposal");
            }
            _cts.Dispose();
            _invoices.Writer.TryComplete();
            _logger.LogInformation("Listener disposed");
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            _logger.LogInformation("WaitInvoice called, waiting for payment notification");
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
                var invoice = await _invoices.Reader.ReadAsync(linkedCts.Token);
                
                // Double-check the invoice status to ensure we have the latest data
                var latestInvoice = await _lightningClient.GetInvoice(invoice.Id, linkedCts.Token);
                if (latestInvoice != null && latestInvoice.Status == LightningInvoiceStatus.Paid)
                {
                    _logger.LogInformation("Confirmed payment for invoice {InvoiceId}. Status: {Status}, AmountReceived: {AmountReceived}, Preimage: {Preimage}", 
                        latestInvoice.Id, latestInvoice.Status, latestInvoice.AmountReceived, latestInvoice.Preimage);

                    // Create a new invoice object with all required fields set
                    var paidInvoice = new LightningInvoice
                    {
                        Id = latestInvoice.Id,
                        BOLT11 = latestInvoice.BOLT11,
                        Status = LightningInvoiceStatus.Paid,
                        Amount = latestInvoice.Amount,
                        AmountReceived = latestInvoice.Amount, // Always set AmountReceived to Amount for paid invoices
                        ExpiresAt = latestInvoice.ExpiresAt,
                        PaymentHash = latestInvoice.PaymentHash,
                        PaidAt = DateTimeOffset.UtcNow,
                        Preimage = latestInvoice.Preimage ?? "00" // Ensure preimage is not null
                    };
                    
                    _logger.LogInformation("Returning paid invoice to BTCPay with Status: {Status}, AmountReceived: {AmountReceived}, PaidAt: {PaidAt}, Preimage: {Preimage}", 
                        paidInvoice.Status, paidInvoice.AmountReceived, paidInvoice.PaidAt, paidInvoice.Preimage);
                    return paidInvoice;
                }
                
                _logger.LogWarning("Invoice {InvoiceId} was marked as paid but latest check shows Status: {Status}. Using original notification data.", 
                    invoice.Id, latestInvoice?.Status);
                return invoice;
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("WaitInvoice cancelled because listener was disposed");
                throw new Exception("Listener has been disposed");
            }
        }
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetInfo");
        await Task.CompletedTask;
        throw new NotSupportedException();
    }



    

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(payParams: {payParams})", payParams);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11}, payParams: {payParams})", bolt11, payParams);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }
    

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("Pay(bolt11: {bolt11})", bolt11);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }


    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("CancelInvoice(invoiceId: {invoiceId})", invoiceId);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("GetDepositAddress()");
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("OpenChannel(request: {request})", openChannelRequest);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ConnectTo(nodeInfo: {nodeInfo})", nodeInfo);
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        Logger.LogInformation("ListChannels()");
        await Task.CompletedTask;
        throw new NotImplementedException();
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
                    Logger.LogInformation("Using cached balance from {LastCheck}", _lastBalanceCheck);
                    return _cachedBalance;
                }

                Logger.LogInformation("Getting balance from BareBitcoin (cache expired or not set)");
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
        await _requestLock.WaitAsync();
        try 
        {
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

            var response = await _httpClient.SendAsync(request);
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
        finally
        {
            _requestLock.Release();
        }
    }

}
