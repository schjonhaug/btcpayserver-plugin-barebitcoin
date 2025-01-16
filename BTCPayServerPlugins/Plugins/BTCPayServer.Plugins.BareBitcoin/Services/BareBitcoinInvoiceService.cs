#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class BareBitcoinInvoiceService
{
    private readonly HashSet<string> _knownInvoiceIds = new HashSet<string>();
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private readonly ILogger _logger;

    public BareBitcoinInvoiceService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task TrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            _knownInvoiceIds.Add(invoiceId);
            _logger.LogInformation("Added invoice {InvoiceId} to tracking list (now tracking {Count} invoices)", 
                invoiceId, _knownInvoiceIds.Count);
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    public async Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            if (_knownInvoiceIds.Contains(invoiceId))
            {
                _knownInvoiceIds.Remove(invoiceId);
                _logger.LogInformation("Removed invoice {InvoiceId} from tracking list (now tracking {Count} invoices)", 
                    invoiceId, _knownInvoiceIds.Count);
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    public async Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            return new HashSet<string>(_knownInvoiceIds);
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }
} 