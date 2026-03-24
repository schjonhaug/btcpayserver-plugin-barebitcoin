#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Singleton service that maintains the central registry of invoices that need to be tracked for payment status.
/// This service acts as the source of truth for which invoices should be monitored across all listener instances.
/// Provides thread-safe operations for adding, removing, and querying tracked invoices.
/// Tracked invoices are persisted to disk so they survive server restarts.
/// </summary>
public class BareBitcoinInvoiceService
{
    private readonly HashSet<string> _trackedInvoiceRegistry = new HashSet<string>();
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private readonly ILogger _logger;
    private readonly string _dataFilePath;

    public BareBitcoinInvoiceService(ILogger logger, string dataFilePath)
    {
        _logger = logger;
        _dataFilePath = dataFilePath;
        LoadFromDisk();
    }

    /// <summary>
    /// Adds an invoice to the central tracking registry and persists the change to disk.
    /// </summary>
    public async Task TrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            if (_trackedInvoiceRegistry.Add(invoiceId))
            {
                _logger.LogDebug("Added invoice {InvoiceId} to tracking registry (now tracking {Count} invoices)",
                    invoiceId, _trackedInvoiceRegistry.Count);
                SaveToDisk();
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Removes an invoice from the central tracking registry and persists the change to disk.
    /// </summary>
    public async Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            if (_trackedInvoiceRegistry.Remove(invoiceId))
            {
                _logger.LogDebug("Removed invoice {InvoiceId} from tracking registry (now tracking {Count} invoices)",
                    invoiceId, _trackedInvoiceRegistry.Count);
                SaveToDisk();
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Returns a copy of the current tracking registry.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            return new HashSet<string>(_trackedInvoiceRegistry);
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataFilePath))
            {
                _logger.LogDebug("No tracked invoices file found at {Path}, starting with empty registry", _dataFilePath);
                return;
            }

            var json = File.ReadAllText(_dataFilePath);
            var invoiceIds = JsonConvert.DeserializeObject<string[]>(json);
            if (invoiceIds != null)
            {
                foreach (var id in invoiceIds)
                    _trackedInvoiceRegistry.Add(id);
                _logger.LogInformation("Loaded {Count} tracked invoices from disk", _trackedInvoiceRegistry.Count);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tracked invoices file at {Path}, starting with empty registry", _dataFilePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read tracked invoices file at {Path}, starting with empty registry", _dataFilePath);
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var directory = Path.GetDirectoryName(_dataFilePath);
            if (directory != null)
                Directory.CreateDirectory(directory);

            var tmpPath = _dataFilePath + ".tmp";
            var json = JsonConvert.SerializeObject(_trackedInvoiceRegistry);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _dataFilePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to persist tracked invoices to {Path}", _dataFilePath);
        }
    }
}
