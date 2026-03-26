#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Handles the monitoring of invoices by polling their status and notifying when payments are detected.
/// This implementation uses a polling approach with a bounded channel for payment notifications.
/// </summary>
public class BareBitcoinListener : ILightningInvoiceListener
{
    private readonly ILightningClient _lightningClient;
    private readonly IBareBitcoinInvoiceService _invoiceService;

    // Channel for communicating paid invoices back to BTCPay Server
    // Uses a bounded channel with a capacity of 100 to prevent memory issues
    private readonly Channel<LightningInvoice> _invoices;

    // Cancellation and task management
    private readonly CancellationTokenSource _cts;
    private readonly Task _pollingTask;
    private readonly ILogger _logger;
    private readonly LogThrottle _persistenceWarningThrottle;

    // Guards against duplicate paid invoice delivery when UntrackInvoice fails.
    // LinkedList tracks insertion order for FIFO eviction; Dictionary provides O(1) lookup and removal.
    private readonly LinkedList<string> _deliveredPaidInvoicesOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _deliveredPaidInvoices = new();
    private readonly int _maxDeliveredCapacity;

    private bool _isDisposed;

    // Limits the number of concurrent GetInvoice API calls to avoid rate limiting
    private readonly int _maxPollConcurrency;

    // Adaptive backoff state
    private int _consecutiveHighErrorCycles;
    private static readonly TimeSpan BasePollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(30);
    private const double ErrorThreshold = 0.5; // 50% failure rate triggers backoff

    internal TimeSpan CurrentPollDelay { get; private set; } = TimeSpan.FromSeconds(2);
    internal Task PollingTask => _pollingTask;

    public bool IsDisposed => _isDisposed;

    internal Action<LightningInvoice>? OnBeforeWrite { get; }
    internal Action<LightningInvoice>? OnAfterWrite { get; }
    internal Action? OnPollCycleCompleted { get; }

    /// <summary>
    /// Initializes a new instance of the BareBitcoinListener.
    /// Sets up the bounded channel and starts the polling task.
    /// </summary>
    public BareBitcoinListener(ILightningClient lightningClient, IBareBitcoinInvoiceService invoiceService, ILogger logger, int maxPollConcurrency = 10)
        : this(lightningClient, invoiceService, logger, channelCapacity: 100, maxPollConcurrency: maxPollConcurrency) { }

    internal BareBitcoinListener(ILightningClient lightningClient, IBareBitcoinInvoiceService invoiceService, ILogger logger, int channelCapacity, int maxPollConcurrency = 10, int maxDeliveredCapacity = 10_000, Action<LightningInvoice>? onBeforeWrite = null, Action<LightningInvoice>? onAfterWrite = null, Action? onPollCycleCompleted = null)
    {
        if (channelCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(channelCapacity));
        if (maxPollConcurrency is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxPollConcurrency));
        if (maxDeliveredCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(maxDeliveredCapacity));

        _lightningClient = lightningClient;
        _invoiceService = invoiceService;
        _logger = logger;
        _maxPollConcurrency = maxPollConcurrency;
        _maxDeliveredCapacity = maxDeliveredCapacity;
        _persistenceWarningThrottle = new LogThrottle(logger, TimeSpan.FromMinutes(5));
        _cts = new CancellationTokenSource();
        OnBeforeWrite = onBeforeWrite;
        OnAfterWrite = onAfterWrite;
        OnPollCycleCompleted = onPollCycleCompleted;

        // Initialize bounded channel with single reader/writer for thread safety
        _invoices = Channel.CreateBounded<LightningInvoice>(new BoundedChannelOptions(channelCapacity)
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
    /// 1. Fetches the current tracked invoices from the central registry
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
                _logger.LogDebug("Polling {Count} tracked invoices for updates", trackedInvoices.Count);
                var results = new ConcurrentBag<(string invoiceId, LightningInvoice? invoice)>();
                var failureCount = 0;

                await Parallel.ForEachAsync(trackedInvoices, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _maxPollConcurrency,
                    CancellationToken = _cts.Token
                }, async (invoiceId, token) =>
                {
                    try
                    {
                        _logger.LogDebug("Polling invoice {InvoiceId}", invoiceId);
                        var invoice = await _lightningClient.GetInvoice(invoiceId, token);
                        results.Add((invoiceId, invoice));
                    }
                    catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                    {
                        throw; // Shutdown requested, propagate to stop the loop
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failureCount);
                        _logger.LogWarning(ex, "Failed to poll invoice {InvoiceId}, will retry next cycle", invoiceId);
                    }
                });

                // Process results sequentially
                foreach (var (invoiceId, invoice) in results)
                {
                    if (invoice == null)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} no longer exists, removing from tracking list", invoiceId);
                        if (await TryUntrackInvoice(invoiceId))
                            RemoveDeliveredInvoice(invoiceId);
                        continue;
                    }

                    _logger.LogDebug("Invoice {InvoiceId} status: {Status}", invoiceId, invoice.Status);
                    if (invoice.Status == LightningInvoiceStatus.Paid)
                    {
                        if (!_deliveredPaidInvoices.ContainsKey(invoiceId))
                        {
                            _logger.LogInformation("Invoice {InvoiceId} has been paid, writing to channel", invoice.Id);
                            OnBeforeWrite?.Invoke(invoice);
                            await _invoices.Writer.WriteAsync(invoice, _cts.Token);
                            OnAfterWrite?.Invoke(invoice);
                            if (_deliveredPaidInvoices.Count >= _maxDeliveredCapacity)
                            {
                                var target = Math.Max(1, _maxDeliveredCapacity / 10);
                                var evicted = 0;
                                for (; evicted < target && _deliveredPaidInvoicesOrder.First != null; evicted++)
                                {
                                    var oldest = _deliveredPaidInvoicesOrder.First!;
                                    _deliveredPaidInvoices.Remove(oldest.Value);
                                    _deliveredPaidInvoicesOrder.RemoveFirst();
                                }

                                _logger.LogDebug(
                                    "Evicted {EvictedCount} oldest entries from delivered paid invoices set (capacity: {Capacity})",
                                    evicted, _maxDeliveredCapacity);
                            }

                            var node = _deliveredPaidInvoicesOrder.AddLast(invoiceId);
                            _deliveredPaidInvoices[invoiceId] = node;
                        }
                        if (await TryUntrackInvoice(invoiceId))
                            RemoveDeliveredInvoice(invoiceId);
                    }
                    else if (invoice.Status == LightningInvoiceStatus.Expired)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} has expired, removing from tracking list", invoiceId);
                        if (await TryUntrackInvoice(invoiceId))
                            RemoveDeliveredInvoice(invoiceId);
                    }
                }

                // Adaptive backoff: increase delay when error rate is high
                if (trackedInvoices.Count > 0)
                {
                    var errorRate = failureCount / (double)trackedInvoices.Count;
                    if (errorRate >= ErrorThreshold)
                    {
                        _consecutiveHighErrorCycles++;
                        CurrentPollDelay = TimeSpan.FromSeconds(Math.Min(
                            BasePollDelay.TotalSeconds * Math.Pow(2, _consecutiveHighErrorCycles),
                            MaxPollDelay.TotalSeconds));
                        _logger.LogWarning("High error rate ({ErrorRate:P0}), backing off to {Delay}s", errorRate, CurrentPollDelay.TotalSeconds);
                    }
                    else
                    {
                        if (_consecutiveHighErrorCycles > 0)
                            _logger.LogInformation("Error rate recovered, resuming normal polling interval");
                        _consecutiveHighErrorCycles = 0;
                        CurrentPollDelay = BasePollDelay;
                    }
                }
                else if (_consecutiveHighErrorCycles > 0)
                {
                    // Reset backoff when idle to ensure prompt polling for newly tracked invoices
                    _consecutiveHighErrorCycles = 0;
                    CurrentPollDelay = BasePollDelay;
                }

                // Wait before next polling cycle
                _logger.LogDebug("Polling cycle complete, waiting {Delay}s before next cycle", CurrentPollDelay.TotalSeconds);
                try { OnPollCycleCompleted?.Invoke(); }
                catch (Exception ex) { _logger.LogDebug(ex, "OnPollCycleCompleted callback threw"); }
                await Task.Delay(CurrentPollDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Polling cancelled");
                break;
            }
            catch (ChannelClosedException)
            {
                _logger.LogDebug("Invoice channel closed, stopping polling");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while polling for invoice updates");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void RemoveDeliveredInvoice(string invoiceId)
    {
        if (_deliveredPaidInvoices.Remove(invoiceId, out var node))
            _deliveredPaidInvoicesOrder.Remove(node);
    }

    private async Task<bool> TryUntrackInvoice(string invoiceId)
    {
        try
        {
            await _invoiceService.UntrackInvoice(invoiceId, _cts.Token);
            return true;
        }
        catch (IOException ex)
        {
            _persistenceWarningThrottle.LogWarning(ex, "Failed to persist untracking for invoice {InvoiceId}, will retry on next poll cycle", invoiceId);
            return false;
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