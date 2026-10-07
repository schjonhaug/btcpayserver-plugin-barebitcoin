#nullable enable
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Singleton registry of live listeners per tracked invoice scope.
/// BTCPay keeps one listener per connection string, so re-saving a store's Lightning setup can leave the
/// old and the new connection listening on the same scope side by side. Only BTCPay knows which of them
/// waits for a given invoice, so a paid invoice is offered to every live listener of its scope; each
/// BTCPay consumer ignores invoice IDs it does not wait for.
/// </summary>
public sealed class BareBitcoinListenerHub
{
    private readonly Dictionary<BareBitcoinInvoiceScope, List<BareBitcoinListener>> _listeners = new();
    private readonly object _lock = new();

    internal void Subscribe(BareBitcoinInvoiceScope scope, BareBitcoinListener listener)
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
        }
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
