#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Singleton service that maintains the central registry of invoices that need to be tracked for payment status.
/// This service acts as the source of truth for which invoices should be monitored across all listener instances.
/// Provides thread-safe operations for adding, removing, and querying tracked invoices.
/// </summary>
public class BareBitcoinInvoiceService
{
    // Central registry of all invoices that need tracking
    // This is the source of truth that all BareBitcoinListener instances will query
    // to know which invoices to monitor
    private readonly HashSet<string> _trackedInvoiceRegistry = new HashSet<string>();
    
    // Ensures thread-safe access to the invoice registry
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private ILogger _logger;

    public ILogger Logger
    {
        get => _logger;
        set => _logger = value;
    }

    public BareBitcoinInvoiceService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Adds an invoice to the central tracking registry.
    /// All active listeners will start monitoring this invoice during their next polling cycle
    /// when they refresh their working copy from this registry.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice to track</param>
    /// <param name="cancellation">Optional cancellation token</param>
    public async Task TrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            _trackedInvoiceRegistry.Add(invoiceId);
            _logger.LogDebug("Added invoice {InvoiceId} to tracking registry (now tracking {Count} invoices)", 
                invoiceId, _trackedInvoiceRegistry.Count);
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Removes an invoice from the central tracking registry.
    /// All active listeners will stop monitoring this invoice during their next polling cycle
    /// when they refresh their working copy from this registry.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice to stop tracking</param>
    /// <param name="cancellation">Optional cancellation token</param>
    public async Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            if (_trackedInvoiceRegistry.Contains(invoiceId))
            {
                _trackedInvoiceRegistry.Remove(invoiceId);
                _logger.LogDebug("Removed invoice {InvoiceId} from tracking registry (now tracking {Count} invoices)", 
                    invoiceId, _trackedInvoiceRegistry.Count);
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Returns a copy of the current tracking registry.
    /// Listeners use this to refresh their working copy of tracked invoices during each polling cycle.
    /// Returns a new HashSet to prevent external modification of the registry.
    /// </summary>
    /// <param name="cancellation">Optional cancellation token</param>
    /// <returns>A read-only collection of tracked invoice IDs</returns>
    public async Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            // Return a new HashSet to prevent external modification of the registry
            return new HashSet<string>(_trackedInvoiceRegistry);
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }
} 