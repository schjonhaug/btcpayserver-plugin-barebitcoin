#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Singleton registry of live listeners per tracked invoice scope.
/// BTCPay keeps one listener per connection string, so re-saving a store's Lightning setup can leave the
/// old and the new connection listening on the same scope side by side. Only BTCPay knows which of them
/// waits for a given invoice, so a paid invoice is offered to every live listener of its scope; each
/// BTCPay consumer ignores invoice IDs it does not wait for.
/// Recently paid invoices are also handed to listeners that subscribe later, so a listener joining while a
/// payment is delivered and untracked still receives it.
/// </summary>
public sealed class BareBitcoinListenerHub
{
    internal const int RecentlyPaidCapacity = 100;
    internal static readonly TimeSpan RecentlyPaidLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<BareBitcoinInvoiceScope, List<BareBitcoinListener>> _listeners = new();
    private readonly Dictionary<BareBitcoinInvoiceScope, LinkedList<RecentlyPaid>> _recentlyPaid = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    public BareBitcoinListenerHub() : this(TimeProvider.System) { }

    internal BareBitcoinListenerHub(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    internal readonly record struct RecentlyPaid(string InvoiceId, LightningInvoice Invoice, DateTimeOffset PaidSeenAt);

    /// <summary>
    /// Adds the listener to its scope and returns the scope's recently paid invoices, which it must deliver too.
    /// </summary>
    internal IReadOnlyList<RecentlyPaid> Subscribe(BareBitcoinInvoiceScope scope, BareBitcoinListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_lock)
        {
            if (!_listeners.TryGetValue(scope, out var listeners))
            {
                listeners = new List<BareBitcoinListener>();
                _listeners[scope] = listeners;
            }

            if (!listeners.Contains(listener))
                listeners.Add(listener);

            if (!_recentlyPaid.TryGetValue(scope, out var recent))
                return Array.Empty<RecentlyPaid>();

            PruneExpired(recent);
            if (recent.Count == 0)
                _recentlyPaid.Remove(scope);
            return recent.ToArray();
        }
    }

    /// <summary>
    /// Records a paid invoice and returns the listeners to offer it to, in one step: a listener subscribing
    /// at the same time is either in the returned snapshot or receives the invoice when it subscribes.
    /// </summary>
    internal IReadOnlyList<BareBitcoinListener> RecordPaid(BareBitcoinInvoiceScope scope, string invoiceId, LightningInvoice invoice)
    {
        lock (_lock)
        {
            if (!_recentlyPaid.TryGetValue(scope, out var recent))
            {
                recent = new LinkedList<RecentlyPaid>();
                _recentlyPaid[scope] = recent;
            }

            PruneExpired(recent);
            if (!recent.Any(entry => entry.InvoiceId == invoiceId))
            {
                if (recent.Count >= RecentlyPaidCapacity)
                    recent.RemoveFirst();
                recent.AddLast(new RecentlyPaid(invoiceId, invoice, _timeProvider.GetUtcNow()));
            }

            return _listeners.TryGetValue(scope, out var listeners)
                ? listeners.ToArray()
                : Array.Empty<BareBitcoinListener>();
        }
    }

    private void PruneExpired(LinkedList<RecentlyPaid> recent)
    {
        var cutoff = _timeProvider.GetUtcNow() - RecentlyPaidLifetime;
        while (recent.First is { } oldest && oldest.Value.PaidSeenAt < cutoff)
            recent.RemoveFirst();
    }

    internal void Unsubscribe(BareBitcoinInvoiceScope scope, BareBitcoinListener listener)
    {
        lock (_lock)
        {
            if (!_listeners.TryGetValue(scope, out var listeners))
                return;

            listeners.Remove(listener);
            if (listeners.Count == 0)
                _listeners.Remove(scope);
        }
    }

    internal IReadOnlyList<BareBitcoinListener> GetListeners(BareBitcoinInvoiceScope scope)
    {
        lock (_lock)
        {
            return _listeners.TryGetValue(scope, out var listeners)
                ? listeners.ToArray()
                : Array.Empty<BareBitcoinListener>();
        }
    }
}
