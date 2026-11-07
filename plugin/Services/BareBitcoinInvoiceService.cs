#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Singleton service that maintains store-connection-isolated registries of invoices that need to be tracked for payment status.
/// Each client and listener can only access invoices through its owning BTCPay store connection scope.
/// Provides thread-safe operations for adding, removing, and querying tracked invoices.
/// Tracked invoices are persisted to disk so they survive server restarts.
/// Disk writes are batched to avoid excessive I/O under high invoice throughput.
/// </summary>
public class BareBitcoinInvoiceService : IBareBitcoinInvoiceService, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 4;
    private readonly Dictionary<string, HashSet<string>> _trackedInvoiceRegistry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invoiceOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredInvoices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unassignedLegacyInvoices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _invoiceTrackingLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _diskWriteLock = new SemaphoreSlim(1, 1);
    private readonly ILogger _logger;
    private readonly string _dataFilePath;
    private readonly IDiskWriter _diskWriter;
    private readonly Timer _flushTimer;
    private readonly LogThrottle _logThrottle;
    private long _snapshotVersion;
    private long _writtenVersion;
    private volatile int _consecutiveFlushFailures;
    private string? _diskPersistenceDisabledReason;
    private bool _dirty;
    private volatile bool _disposed;
    private volatile bool _closed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    public string? PersistenceDisabledReason => _diskPersistenceDisabledReason;
    private Exception? _lastFlushException;

    /// <summary>
    /// The exception from the most recent flush failure, or null if the last flush succeeded.
    /// </summary>
    public Exception? LastFlushException => Volatile.Read(ref _lastFlushException);

    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxFlushBackoff = TimeSpan.FromSeconds(30);

    public BareBitcoinInvoiceService(ILogger logger, string dataFilePath, IDiskWriter? diskWriter = null)
    {
        _logger = logger;
        _dataFilePath = dataFilePath;
        _diskWriter = diskWriter ?? new FileDiskWriter(dataFilePath);
        _flushTimer = new Timer(async _ =>
        {
            try { await FlushAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in flush timer callback"); }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _logThrottle = new LogThrottle(logger, TimeSpan.FromMinutes(5));
        LoadFromDisk();
    }

    public async Task<bool> CanAccessInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try { return !_invoiceOwners.TryGetValue(invoiceId, out var owner) || owner == scope.Value; }
        finally { _invoiceTrackingLock.Release(); }
    }

    /// <summary>Recovery inventory; retained terminal ownership is deliberately not automatically deleted.</summary>
    public async Task<(int Active, int Quarantined, int Retired, string? PersistenceError)> GetHealth(CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try { return (_trackedInvoiceRegistry.Values.Sum(ids => ids.Count), _unassignedLegacyInvoices.Count,
            _retiredInvoices.Count, _diskPersistenceDisabledReason ?? LastFlushException?.Message); }
        finally { _invoiceTrackingLock.Release(); }
    }

    /// <summary>
    /// Adds an invoice to the central tracking registry.
    /// The change is persisted to disk asynchronously via a flush timer.
    /// </summary>
    public async Task TrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_invoiceOwners.TryGetValue(invoiceId, out var owningScope) &&
                !StringComparer.Ordinal.Equals(owningScope, scope.Value))
            {
                _logger.LogWarning("Ignored conflicting ownership claim for tracked invoice {InvoiceId}", invoiceId);
                return;
            }

            if (_retiredInvoices.Contains(invoiceId)) return;
            if (!_trackedInvoiceRegistry.TryGetValue(scope.Value, out var invoices))
            {
                invoices = new HashSet<string>(StringComparer.Ordinal);
                _trackedInvoiceRegistry.Add(scope.Value, invoices);
            }

            var scopeChanged = invoices.Add(invoiceId);
            var reclaimedLegacyInvoice = _unassignedLegacyInvoices.Remove(invoiceId);
            if (scopeChanged || reclaimedLegacyInvoice)
            {
                _invoiceOwners[invoiceId] = scope.Value;
                _logger.LogDebug("Added invoice {InvoiceId} to scoped tracking registry (scope now tracks {Count} invoices)",
                    invoiceId, invoices.Count);
                MarkRegistryChanged();
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
    public async Task UntrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_trackedInvoiceRegistry.TryGetValue(scope.Value, out var invoices) && invoices.Remove(invoiceId))
            {
                _retiredInvoices.Add(invoiceId);
                if (invoices.Count == 0)
                    _trackedInvoiceRegistry.Remove(scope.Value);

                _logger.LogDebug("Removed invoice {InvoiceId} from scoped tracking registry (scope now tracks {Count} invoices)",
                    invoiceId, invoices.Count);
                MarkRegistryChanged();
            }
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Atomically claims a quarantined legacy invoice for a scope after a
    /// successful lookup through BTCPay's owning-store connection. Conflicting
    /// claims cannot replace or remove the established owner.
    /// </summary>
    public async Task<bool> TryClaimLegacyInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_unassignedLegacyInvoices.Contains(invoiceId))
                return false;

            if (_invoiceOwners.TryGetValue(invoiceId, out var owningScope) &&
                !StringComparer.Ordinal.Equals(owningScope, scope.Value))
            {
                _logger.LogWarning("Ignored conflicting legacy ownership claim for invoice {InvoiceId}", invoiceId);
                return false;
            }

            if (!_trackedInvoiceRegistry.TryGetValue(scope.Value, out var invoices))
            {
                invoices = new HashSet<string>(StringComparer.Ordinal);
                _trackedInvoiceRegistry.Add(scope.Value, invoices);
            }

            invoices.Add(invoiceId);
            _invoiceOwners[invoiceId] = scope.Value;
            _unassignedLegacyInvoices.Remove(invoiceId);
            _logger.LogDebug("Claimed invoice {InvoiceId} from the legacy quarantine", invoiceId);
            MarkRegistryChanged();
            return true;
        }
        finally
        {
            _invoiceTrackingLock.Release();
        }
    }

    /// <summary>
    /// Returns a copy of the current tracking registry.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> GetTrackedInvoices(BareBitcoinInvoiceScope scope, CancellationToken cancellation = default)
    {
        await _invoiceTrackingLock.WaitAsync(cancellation);
        try
        {
            return _trackedInvoiceRegistry.TryGetValue(scope.Value, out var invoices)
                ? new HashSet<string>(invoices, StringComparer.Ordinal)
                : Array.Empty<string>();
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
        await _diskWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_closed) await FlushCoreAsync().ConfigureAwait(false);
        }
        finally { _diskWriteLock.Release(); }
    }

    private async Task FlushCoreAsync()
    {
        if (_diskPersistenceDisabledReason is not null)
            return;

        try
        {
            if (!await TryAcquireAsync(_invoiceTrackingLock)) return;

            string json;
            long version;
            try
            {
                if (!_dirty) return;
                json = SerializeRegistry();
                _dirty = false;
                version = ++_snapshotVersion;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFlushFailures);
                Volatile.Write(ref _lastFlushException, ex);
                _logThrottle.LogError(ex, "Failed to serialize tracked invoices");
                if (!_disposed)
                    ScheduleFlush(GetFlushBackoff());
                return;
            }
            finally
            {
                SafeRelease(_invoiceTrackingLock);
            }

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
                _logThrottle.LogError(ex, "Failed to flush tracked invoices to disk");
                await MarkDirtyAndRescheduleAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in FlushAsync");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _invoiceTrackingLock.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _invoiceTrackingLock.Release(); }

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

        // Leave managed semaphores usable for already queued timer callbacks.
        // All writes are joined above; late callbacks observe Closed and do nothing.
        _closed = true;
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

    private void MarkRegistryChanged()
    {
        // An unknown future schema must remain byte-for-byte untouched. Continue
        // tracking in memory so invoices created by this process are still polled.
        if (_diskPersistenceDisabledReason is not null)
            return;

        if (!_dirty)
            ScheduleFlush();
        _dirty = true;
    }

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
        var exponent = Math.Clamp(_consecutiveFlushFailures - 1, 0, 10);
        var seconds = FlushInterval.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxFlushBackoff.TotalSeconds));
    }

    private void LoadFromDisk()
    {
        try
        {
            var json = _diskWriter.Read();
            if (json == null)
            {
                _logger.LogDebug("No tracked invoices file found at {Path}, starting with empty registry", _dataFilePath);
                return;
            }

            var token = JToken.Parse(json);
            if (token.Type == JTokenType.Array)
            {
                foreach (var invoiceId in token.Values<string>().Where(id => !string.IsNullOrWhiteSpace(id)))
                    _unassignedLegacyInvoices.Add(invoiceId!);

                _logger.LogWarning(
                    "Quarantined {Count} legacy tracked invoices because the persisted state has no account ownership information; " +
                    "they will remain persisted and unavailable to scoped listeners until BTCPay's owning-store startup reconciliation reclaims or resolves them",
                    _unassignedLegacyInvoices.Count);
                _dirty = true;
                ScheduleFlush();
                return;
            }

            var persisted = token.ToObject<PersistedRegistry>();
            if (persisted?.Version == 2)
            {
                var accountScopedInvoices = persisted.Scopes is null
                    ? Enumerable.Empty<string[]>()
                    : persisted.Scopes.Values;
                foreach (var invoiceId in accountScopedInvoices
                             .Where(invoiceIds => invoiceIds is not null)
                             .SelectMany(invoiceIds => invoiceIds!)
                             .Concat(persisted.UnassignedLegacyInvoices ?? Array.Empty<string>())
                             .Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    _unassignedLegacyInvoices.Add(invoiceId);
                }

                _logger.LogWarning(
                    "Quarantined {Count} invoices from account-scoped registry schema version 2 because it has no BTCPay store ownership information; " +
                    "owning-store startup reconciliation must reclaim them into store-scoped state",
                    _unassignedLegacyInvoices.Count);
                _dirty = true;
                ScheduleFlush();
                return;
            }

            if (persisted is null || persisted.Version is not (3 or CurrentSchemaVersion) || persisted.Scopes is null)
            {
                _diskPersistenceDisabledReason =
                    $"Unsupported tracked invoice registry schema version {persisted?.Version.ToString() ?? "unknown"}";
                _logger.LogError(
                    "Tracked invoice persistence is disabled because {Path} uses unsupported schema version {Version}; " +
                    "the file will remain unchanged and new invoices will be tracked in memory until it is explicitly migrated",
                    _dataFilePath, persisted?.Version.ToString() ?? "unknown");
                return;
            }

            foreach (var (scope, invoiceIds) in persisted.Scopes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(scope) || invoiceIds is null)
                    continue;

                var validInvoiceIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var invoiceId in invoiceIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    if (_invoiceOwners.TryAdd(invoiceId, scope))
                        validInvoiceIds.Add(invoiceId);
                    else if (!StringComparer.Ordinal.Equals(_invoiceOwners[invoiceId], scope))
                        throw new JsonSerializationException("Conflicting persisted invoice ownership; restore authoritative ownership before restarting");
                }

                if (validInvoiceIds.Count > 0)
                    _trackedInvoiceRegistry[scope] = validInvoiceIds;
            }

            foreach (var (invoiceId, owner) in persisted.RetiredOwners ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(invoiceId) || string.IsNullOrWhiteSpace(owner)) continue;
                if (_invoiceOwners.TryGetValue(invoiceId, out var activeOwner) && activeOwner != owner)
                    throw new JsonSerializationException("Conflicting active and terminal invoice ownership");
                _invoiceOwners[invoiceId] = owner;
                _retiredInvoices.Add(invoiceId);
                if (_trackedInvoiceRegistry.TryGetValue(owner, out var active)) active.Remove(invoiceId);
            }

            var assignedInvoiceIds = _trackedInvoiceRegistry.Values
                .SelectMany(invoices => invoices)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var invoiceId in persisted.UnassignedLegacyInvoices ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(invoiceId) && !_invoiceOwners.ContainsKey(invoiceId))
                    _unassignedLegacyInvoices.Add(invoiceId);
            }

            _logger.LogInformation(
                "Loaded {Count} tracked invoices across {ScopeCount} store connection scopes and {LegacyCount} quarantined legacy invoices from disk",
                assignedInvoiceIds.Count, _trackedInvoiceRegistry.Count, _unassignedLegacyInvoices.Count);

            if (_dirty)
            {
                _logger.LogWarning("Removed conflicting duplicate invoice ownership while loading persisted tracking state");
                ScheduleFlush();
            }
        }
        catch (JsonException ex)
        {
            DisablePersistence(ex, "Registry JSON could not be parsed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DisablePersistence(ex, "Registry could not be read");
        }
    }

    private void DisablePersistence(Exception exception, string reason)
    {
        _trackedInvoiceRegistry.Clear();
        _invoiceOwners.Clear();
        _retiredInvoices.Clear();
        _unassignedLegacyInvoices.Clear();
        _dirty = false;
        _diskPersistenceDisabledReason = reason;
        Volatile.Write(ref _lastFlushException, exception);
        _logger.LogError(exception, "{Reason} at {Path}. Persistence is disabled to preserve recovery data; repair the file and restart", reason, _dataFilePath);
    }

    // FlushAsync invokes this while holding _invoiceTrackingLock, so every
    // collection below is snapshotted without concurrent mutation.
    internal virtual string SerializeRegistry()
    {
        var scopes = _trackedInvoiceRegistry
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return JsonConvert.SerializeObject(new PersistedRegistry
        {
            Version = CurrentSchemaVersion,
            Scopes = scopes,
            RetiredOwners = _retiredInvoices.OrderBy(id => id, StringComparer.Ordinal)
                .ToDictionary(id => id, id => _invoiceOwners[id], StringComparer.Ordinal),
            UnassignedLegacyInvoices = _unassignedLegacyInvoices
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
        });
    }

    internal virtual async Task SaveToDiskAsync(string json)
    {
        await _diskWriter.WriteAsync(json);
    }

    private sealed class PersistedRegistry
    {
        [JsonProperty("version")]
        public int Version { get; init; }

        [JsonProperty("scopes")]
        public Dictionary<string, string[]>? Scopes { get; init; }

        [JsonProperty("retiredOwners")]
        public Dictionary<string, string>? RetiredOwners { get; init; }

        [JsonProperty("unassignedLegacyInvoices")]
        public string[]? UnassignedLegacyInvoices { get; init; }
    }
}
