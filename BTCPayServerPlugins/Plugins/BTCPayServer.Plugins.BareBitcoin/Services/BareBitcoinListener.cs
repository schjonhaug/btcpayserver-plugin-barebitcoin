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
/// Handles the monitoring of invoices by polling their status and notifying when payments are detected.
/// Each listener maintains its own working copy of tracked invoices, refreshed from the central registry during each polling cycle.
/// This implementation uses a polling approach with a bounded channel for payment notifications.
/// </summary>
public class BareBitcoinListener : ILightningInvoiceListener
{
    private readonly BareBitcoinLightningClient _lightningClient;
    private readonly BareBitcoinInvoiceService _invoiceService;
    
    // Channel for communicating paid invoices back to BTCPay Server
    // Uses a bounded channel with a capacity of 100 to prevent memory issues
    private readonly Channel<LightningInvoice> _invoices;
    
    // Cancellation and task management
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly Task _pollingTask;
    private readonly ILogger _logger;
    
    // Working copy of tracked invoices, refreshed from the central registry in BareBitcoinInvoiceService
    // during each polling cycle. This is instance-specific and not shared between listeners.
    private readonly HashSet<string> _trackedInvoices = new HashSet<string>();
    private bool _isDisposed;

    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Initializes a new instance of the BareBitcoinListener.
    /// Sets up the bounded channel and starts the polling task.
    /// </summary>
    public BareBitcoinListener(BareBitcoinLightningClient lightningClient, BareBitcoinInvoiceService invoiceService, ILogger logger)
    {
        _lightningClient = lightningClient;
        _invoiceService = invoiceService;
        _logger = logger;
        _cts = new CancellationTokenSource();
        
        // Initialize bounded channel with single reader/writer for thread safety
        _invoices = Channel.CreateBounded<LightningInvoice>(new BoundedChannelOptions(100)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        
        // Start the polling task immediately
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
        _logger.LogDebug("Starting invoice polling task");

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Get the current list of invoices to track
                var trackedInvoices = await _invoiceService.GetTrackedInvoices(_cts.Token);
                _logger.LogDebug("Found {Count} tracked invoices", trackedInvoices.Count);
                
                // Update our tracking list
                _trackedInvoices.Clear();
                foreach (var invoiceId in trackedInvoices)
                {
                    _logger.LogDebug("Adding invoice {InvoiceId} to tracking list", invoiceId);
                    _trackedInvoices.Add(invoiceId);
                }

                // Check each tracked invoice for status updates
                _logger.LogDebug("Polling {Count} tracked invoices for updates", _trackedInvoices.Count);
                foreach (var invoiceId in _trackedInvoices.ToList())
                {
                    _logger.LogDebug("Polling invoice {InvoiceId}", invoiceId);
                    var invoice = await _lightningClient.GetInvoice(invoiceId, _cts.Token);
                    
                    // Handle invoice no longer existing
                    if (invoice == null)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} no longer exists, removing from tracking list", invoiceId);
                        _trackedInvoices.Remove(invoiceId);
                        await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
                        continue;
                    }

                    // Process invoice based on its status
                    _logger.LogDebug("Invoice {InvoiceId} status: {Status}", invoiceId, invoice.Status);
                    if (invoice.Status == LightningInvoiceStatus.Paid)
                    {
                        // Attempt to notify BTCPay Server of the payment via the channel
                        _logger.LogInformation("Invoice {InvoiceId} has been paid, attempting to write to channel", invoice.Id);
                        var writeResult = _invoices.Writer.TryWrite(invoice);
                        _logger.LogDebug("Write to channel for invoice {InvoiceId} result: {Result}", invoice.Id, writeResult);
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

                // Wait before next polling cycle
                _logger.LogDebug("Polling cycle complete, waiting 2 seconds before next cycle");
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Polling cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while polling for invoice updates");
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
            }
        }
    }

    /// <summary>
    /// Implements IDisposable to clean up resources and stop the polling task.
    /// Ensures graceful shutdown of the polling loop and channel.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _logger.LogDebug("Disposing listener");
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
        _logger.LogDebug("Listener disposed");
    }

    /// <summary>
    /// Waits for and returns the next paid invoice notification from the channel.
    /// This method is called by BTCPay Server to receive payment notifications.
    /// </summary>
    /// <param name="cancellation">Token to cancel the wait operation</param>
    /// <returns>The next paid invoice</returns>
    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(BareBitcoinListener));

        _logger.LogDebug("WaitInvoice called, waiting for payment notification");
        try 
        {
            _logger.LogDebug("About to read from channel");
            var invoice = await _invoices.Reader.ReadAsync(cancellation);
            _logger.LogDebug("Successfully read invoice {InvoiceId} with status {Status} from channel", 
                invoice.Id, invoice.Status);
            return invoice;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug("WaitInvoice was cancelled: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WaitInvoice: {Message}", ex.Message);
            throw;
        }
    }
} 