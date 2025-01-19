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

/// <summary>
/// Handles the actual monitoring of invoices by polling their status and notifying when payments are detected.
/// Each listener maintains its own working copy of tracked invoices, refreshed from the central registry during each polling cycle.
/// </summary>
public class BareBitcoinListener : ILightningInvoiceListener
{
    private readonly BareBitcoinLightningClient _lightningClient;
    private readonly BareBitcoinInvoiceService _invoiceService;
    // Channel for communicating paid invoices back to BTCPay Server
    private readonly Channel<LightningInvoice> _invoices;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly Task _pollingTask;
    private readonly ILogger _logger;
    // Working copy of tracked invoices, refreshed from the central registry in BareBitcoinInvoiceService
    // during each polling cycle. This is instance-specific and not shared between listeners.
    private readonly HashSet<string> _trackedInvoices = new HashSet<string>();
    private bool _isDisposed;

    public bool IsDisposed => _isDisposed;

    public BareBitcoinListener(BareBitcoinLightningClient lightningClient, BareBitcoinInvoiceService invoiceService, ILogger logger)
    {
        _lightningClient = lightningClient;
        _invoiceService = invoiceService;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _invoices = Channel.CreateBounded<LightningInvoice>(new BoundedChannelOptions(100)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pollingTask = StartPolling();
    }

    /// <summary>
    /// Main polling loop that monitors tracked invoices for payment status changes.
    /// During each cycle, it:
    /// 1. Refreshes its working copy from the central registry
    /// 2. Checks each tracked invoice for updates
    /// 3. Notifies of any detected payments via the channel
    /// </summary>
    private async Task StartPolling()
    {
        _logger.LogInformation("Starting invoice polling task");

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Get the current list of invoices to track
                var trackedInvoices = await _invoiceService.GetTrackedInvoices(_cts.Token);
                _logger.LogInformation("Found {Count} tracked invoices", trackedInvoices.Count);
                
                // Update our tracking list
                _trackedInvoices.Clear();
                foreach (var invoiceId in trackedInvoices)
                {
                    _logger.LogInformation("Adding invoice {InvoiceId} to tracking list", invoiceId);
                    _trackedInvoices.Add(invoiceId);
                }

                // Check each tracked invoice
                _logger.LogInformation("Polling {Count} tracked invoices for updates", _trackedInvoices.Count);
                foreach (var invoiceId in _trackedInvoices.ToList())
                {
                    var invoice = await _lightningClient.GetInvoice(invoiceId, _cts.Token);
                    
                    if (invoice == null)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} no longer exists, removing from tracking list", invoiceId);
                        _trackedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                        continue;
                    }

                    _logger.LogInformation("Invoice {InvoiceId} status: {Status}", invoiceId, invoice.Status);
                    if (invoice.Status == LightningInvoiceStatus.Paid)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} has been paid, attempting to write to channel", invoice.Id);
                        var writeResult = _invoices.Writer.TryWrite(invoice);
                        _logger.LogInformation("Write to channel for invoice {InvoiceId} result: {Result}", invoice.Id, writeResult);
                        if (!writeResult)
                        {
                            _logger.LogWarning("Failed to write paid invoice {InvoiceId} to channel", invoice.Id);
                        }
                        _trackedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                    }
                    else if (invoice.Status == LightningInvoiceStatus.Expired)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} has expired, removing from tracking list", invoiceId);
                        _trackedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
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
        if (_isDisposed)
            return;

        _logger.LogInformation("Disposing listener");
        _cts.Cancel();
        try
        {
            _pollingTask.Wait(TimeSpan.FromSeconds(5));
            _invoices.Writer.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while waiting for polling task to complete during disposal");
        }
        _cts.Dispose();
        _isDisposed = true;
        _logger.LogInformation("Listener disposed");
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(BareBitcoinListener));

        _logger.LogInformation("WaitInvoice called, waiting for payment notification");
        try 
        {
            _logger.LogInformation("About to read from channel");
            var invoice = await _invoices.Reader.ReadAsync(cancellation);
            _logger.LogInformation("Successfully read invoice {InvoiceId} with status {Status} from channel", 
                invoice.Id, invoice.Status);
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