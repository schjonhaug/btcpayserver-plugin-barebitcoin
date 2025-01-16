#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.BareBitcoin;

public class BareBitcoinLightningClient : ILightningClient
{
    private readonly string _privateKey;
    private readonly string _publicKey;
    private readonly string _accountId; 
    private readonly Uri _apiEndpoint;
    private readonly HttpClient _httpClient;
    private readonly Network _network;
    private readonly BareBitcoinApiService _apiService;
    private readonly BareBitcoinBalanceService _balanceService;
    private readonly BareBitcoinInvoiceService _invoiceService;
    public ILogger Logger;

    private ILightningInvoiceListener? _currentListener;
    private readonly SemaphoreSlim _listenerLock = new SemaphoreSlim(1, 1);

    public BareBitcoinLightningClient(string privateKey, string publicKey, string accountId, Uri apiEndpoint, Network network, HttpClient httpClient, ILogger logger)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
        _accountId = accountId;
        _apiEndpoint = apiEndpoint;
        _network = network;
        _httpClient = httpClient;
        Logger = logger;
        
        _apiService = new BareBitcoinApiService(_privateKey, _publicKey, _httpClient, logger);
        _balanceService = new BareBitcoinBalanceService(_apiService, logger);
        _invoiceService = new BareBitcoinInvoiceService(logger);
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
            var response = await _apiService.MakeAuthenticatedRequest("GET", $"/v1/deposit-destinations/bitcoin/invoice/{invoiceId}");
            var responseObj = JObject.Parse(response);

            var invoice = responseObj["invoice"]?.Value<string>();
            if (string.IsNullOrEmpty(invoice))
            {
                Logger.LogWarning("Invoice {InvoiceId} not found or empty response", invoiceId);
                return null;
            }

            var status = responseObj["status"]?.Value<string>() switch
            {
                "INVOICE_STATUS_UNPAID" => LightningInvoiceStatus.Unpaid,
                "INVOICE_STATUS_PAID" => LightningInvoiceStatus.Paid,
                "INVOICE_STATUS_EXPIRED" => LightningInvoiceStatus.Expired,
                "INVOICE_STATUS_CANCELED" => LightningInvoiceStatus.Expired,
                _ => LightningInvoiceStatus.Unpaid // Default case
            };

            var bolt11 = BOLT11PaymentRequest.Parse(invoice, _network);
            var amount = bolt11.MinimumAmount;
            var paymentHash = bolt11.PaymentHash?.ToString() ?? string.Empty;
            var paidAt = status == LightningInvoiceStatus.Paid ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            var amountReceived = status == LightningInvoiceStatus.Paid ? amount : null;
            var preimage = status == LightningInvoiceStatus.Paid ? 
                (responseObj["preimage"]?.Value<string>() ?? paymentHash) : 
                null;

            // Only remove expired invoices from tracking - paid ones will be removed after notification
            if (status == LightningInvoiceStatus.Expired)
            {
                await _invoiceService.UntrackInvoice(invoiceId, cancellation);
            }

            var result = new LightningInvoice
            {
                Id = invoiceId,
                BOLT11 = invoice,
                Status = status,
                Amount = amount,
                AmountReceived = amountReceived,
                ExpiresAt = bolt11.ExpiryDate,
                PaymentHash = paymentHash,
                PaidAt = paidAt,
                Preimage = preimage
            };

            Logger.LogInformation("Returning invoice {InvoiceId} with status {Status}, AmountReceived: {AmountReceived}, PaymentHash: {PaymentHash}, Preimage: {Preimage}", 
                result.Id, result.Status, result.AmountReceived, result.PaymentHash, result.Preimage);

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
            var invoices = new List<LightningInvoice>();
            var isPendingOnly = request.PendingOnly.GetValueOrDefault(false);

            var trackedInvoices = await _invoiceService.GetTrackedInvoices(cancellation);
            foreach (var invoiceId in trackedInvoices)
            {
                var invoice = await GetInvoice(invoiceId, cancellation);
                if (invoice != null)
                {
                    if (!isPendingOnly || invoice.Status == LightningInvoiceStatus.Unpaid)
                    {
                        Logger.LogInformation("Adding invoice {InvoiceId} with status {Status} to results", 
                            invoice.Id, invoice.Status);
                        invoices.Add(invoice);
                    }
                }
            }

            Logger.LogInformation("Found {Count} matching invoices (PendingOnly: {PendingOnly})", 
                invoices.Count, isPendingOnly);

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

            var response = await _apiService.MakeAuthenticatedRequest(
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
            await _invoiceService.TrackInvoice(invoiceId, cancellation);

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
            // If we have a current listener, check if it's still active
            if (_currentListener is BareBitcoinListener listener)
            {
                Logger.LogInformation("Returning existing listener");
                return listener;
            }

            // If we get here, either _currentListener is null or it's been disposed
            Logger.LogInformation("Creating new listener");
            _currentListener = new BareBitcoinListener(this, _invoiceService, Logger);
            return _currentListener;
        }
        finally
        {
            _listenerLock.Release();
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
        return await _balanceService.GetBalance(_accountId, cancellation);
    }
}
