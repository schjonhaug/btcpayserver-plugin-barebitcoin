#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class BareBitcoinListener : ILightningInvoiceListener
{
    private readonly BareBitcoinLightningClient _lightningClient;
    private readonly BareBitcoinInvoiceService _invoiceService;
    private static readonly Channel<LightningInvoice> _invoices = Channel.CreateUnbounded<LightningInvoice>(new UnboundedChannelOptions 
    { 
        SingleReader = false,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly Task _pollingTask;
    private readonly ILogger _logger;
    private readonly HashSet<string> _watchedInvoices = new HashSet<string>();

    public BareBitcoinListener(BareBitcoinLightningClient lightningClient, BareBitcoinInvoiceService invoiceService, ILogger logger)
    {
        _lightningClient = lightningClient;
        _invoiceService = invoiceService;
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
                var trackedInvoices = await _invoiceService.GetTrackedInvoices(_cts.Token);
                foreach (var invoiceId in trackedInvoices)
                {
                    if (!_watchedInvoices.Contains(invoiceId))
                    {
                        _logger.LogInformation("Adding invoice {InvoiceId} to watch list", invoiceId);
                        _watchedInvoices.Add(invoiceId);
                    }
                }

                // Check each watched invoice
                _logger.LogInformation("Polling {Count} watched invoices for updates", _watchedInvoices.Count);
                foreach (var invoiceId in _watchedInvoices.ToList())
                {
                    var invoice = await _lightningClient.GetInvoice(invoiceId, _cts.Token);
                    
                    if (invoice == null)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} no longer exists, removing from watch list", invoiceId);
                        _watchedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                        continue;
                    }

                    if (invoice.Status == LightningInvoiceStatus.Paid)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} has been paid, attempting to write to channel", invoice.Id);
                        var writeResult = _invoices.Writer.TryWrite(invoice);
                        _logger.LogInformation("Write to channel for invoice {InvoiceId} result: {Result}", invoice.Id, writeResult);
                        if (!writeResult)
                        {
                            _logger.LogWarning("Failed to write paid invoice {InvoiceId} to channel", invoice.Id);
                        }
                        _watchedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                    }
                    else if (invoice.Status == LightningInvoiceStatus.Expired)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} has expired, removing from watch list", invoiceId);
                        _watchedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                    }
                    else
                    {
                        _logger.LogInformation("Invoice {InvoiceId} status: {Status}", invoiceId, invoice.Status);
                    }
                }

                _logger.LogInformation("Polling cycle complete, waiting 2 seconds before next cycle");
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Polling cancelled");
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
        // Do not complete the channel writer when disposing - it's shared between listeners
        _logger.LogInformation("Listener disposed");
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        _logger.LogInformation("WaitInvoice called, waiting for payment notification");
        try 
        {
            _logger.LogInformation("Starting to read from channel (Reader.Count: {Count})", _invoices.Reader.Count);
            _logger.LogInformation("About to call ReadAsync on channel");
            var invoice = await _invoices.Reader.ReadAsync(cancellation);
            _logger.LogInformation("ReadAsync completed successfully");
            _logger.LogInformation("Successfully read invoice {InvoiceId} with status {Status} from channel (Reader.Count: {Count})", 
                invoice.Id, invoice.Status, _invoices.Reader.Count);
            return invoice;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("WaitInvoice was cancelled: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WaitInvoice: {Message}", ex.Message);
            throw;
        }
    }
} 