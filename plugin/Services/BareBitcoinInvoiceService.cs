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
/// Disk writes are batched to avoid excessive I/O under high invoice throughput.
/// </summary>
public class BareBitcoinInvoiceService : IBareBitcoinInvoiceService, IAsyncDisposable
{
    private readonly HashSet<string> _trackedInvoiceRegistry = new HashSet<string>();
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _diskWriteLock = new SemaphoreSlim(1, 1);
    private readonly ILogger _logger;
    private readonly string _dataFilePath;
    private readonly Timer _flushTimer;
    private readonly LogThrottle _logThrottle;
    private long _snapshotVersion;
    private long _writtenVersion;
    private volatile int _consecutiveFlushFailures;
    private bool _dirty;
    private bool _disposed;
    private Exception? _lastFlushException;

    /// <summary>
    /// The exception from the most recent flush failure, or null if the last flush succeeded.
    /// </summary>
    public Exception? LastFlushException => Volatile.Read(ref _lastFlushException);

    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxFlushBackoff = TimeSpan.FromSeconds(30);

    public BareBitcoinInvoiceService(ILogger logger, string dataFilePath)
    {
        _logger = logger;
        _dataFilePath = dataFilePath;
        _flushTimer = new Timer(async _ =>
        {
            try { await FlushAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in flush timer callback"); }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _logThrottle = new LogThrottle(logger, TimeSpan.FromMinutes(5));
        LoadFromDisk();
    }

    /// <summary>
    /// Adds an invoice to the central tracking registry.
    /// The change is persisted to disk asynchronously via a flush timer.
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
                if (!_dirty)
                    ScheduleFlush();
                _dirty = true;
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Removes an invoice from the central tracking registry.
    /// The change is persisted to disk asynchronously via a flush timer.
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
                if (!_dirty)
                    ScheduleFlush();
                _dirty = true;
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

    /// <summary>
    /// Immediately persists any dirty in-memory state to disk.
    /// </summary>
    public async Task FlushAsync()
    {
        try
        {
            if (!await TryAcquireAsync(_invoiceTrackingLock)) return;

            string json;
            long version;
            try
            {
                if (!_dirty) return;
                json = JsonConvert.SerializeObject(_trackedInvoiceRegistry);
                _dirty = false;
                version = ++_snapshotVersion;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFlushFailures);
                _logThrottle.LogWarning(ex, "Failed to serialize tracked invoices");
                if (!_disposed)
                    ScheduleFlush(GetFlushBackoff());
                return;
            }
            finally
            {
                SafeRelease(_invoiceTrackingLock);
            }

            if (!await TryAcquireAsync(_diskWriteLock)) return;
            try
            {
                if (version <= _writtenVersion) return;
                await SaveToDiskAsync(json);
                _writtenVersion = version;
                Interlocked.Exchange(ref _consecutiveFlushFailures, 0);
                Volatile.Write(ref _lastFlushException, null);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFlushFailures);
                Volatile.Write(ref _lastFlushException, ex);
                _logThrottle.LogWarning(ex, "Failed to flush tracked invoices to disk");
                await MarkDirtyAndRescheduleAsync();
            }
            finally
            {
                SafeRelease(_diskWriteLock);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in FlushAsync");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        await FlushAsync();

        if (Volatile.Read(ref _lastFlushException) != null)
        {
            _logger.LogWarning("Retrying final flush after initial failure during dispose");
            await Task.Delay(100);
            await FlushAsync();
        }

        var finalException = Volatile.Read(ref _lastFlushException);
        if (finalException != null)
        {
            _logger.LogError(finalException,
                "Final flush failed during dispose — tracked invoice state may be lost on restart");
        }

        _invoiceTrackingLock.Dispose();
        _diskWriteLock.Dispose();
    }

    private static async Task<bool> TryAcquireAsync(SemaphoreSlim semaphore)
    {
        try
        {
            await semaphore.WaitAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void SafeRelease(SemaphoreSlim semaphore)
    {
        try { semaphore.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task MarkDirtyAndRescheduleAsync()
    {
        if (!await TryAcquireAsync(_invoiceTrackingLock)) return;
        try
        {
            _dirty = true;
            if (!_disposed)
                ScheduleFlush(GetFlushBackoff());
        }
        finally
        {
            SafeRelease(_invoiceTrackingLock);
        }
    }

    internal int ConsecutiveFlushFailures => _consecutiveFlushFailures;

    private void ScheduleFlush() => ScheduleFlush(FlushInterval);

    private void ScheduleFlush(TimeSpan delay)
    {
        try
        {
            _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }

    internal TimeSpan GetFlushBackoff()
    {
        var exponent = Math.Min(_consecutiveFlushFailures - 1, 10);
        var seconds = FlushInterval.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxFlushBackoff.TotalSeconds));
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
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        _trackedInvoiceRegistry.Add(id);
                }
                _logger.LogInformation("Loaded {Count} tracked invoices from disk", _trackedInvoiceRegistry.Count);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tracked invoices file at {Path}, starting with empty registry", _dataFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read tracked invoices file at {Path}, starting with empty registry", _dataFilePath);
        }
    }

    internal virtual async Task SaveToDiskAsync(string json)
    {
        var directory = Path.GetDirectoryName(_dataFilePath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        var tmpPath = _dataFilePath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json);
        File.Move(tmpPath, _dataFilePath, overwrite: true);
    }
}
