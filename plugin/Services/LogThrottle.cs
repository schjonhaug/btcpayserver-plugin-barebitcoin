#nullable enable
using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Throttles repeated log warnings by message template, logging the first occurrence
/// immediately and suppressing duplicates for a configurable window.
/// </summary>
internal class LogThrottle
{
    private readonly ILogger _logger;
    private readonly TimeSpan _suppressionWindow;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, ThrottleState> _states = new();

    internal class ThrottleState
    {
        public DateTimeOffset WindowStart;
        public int SuppressedCount;
        public readonly object Lock = new();
    }

    public LogThrottle(ILogger logger, TimeSpan suppressionWindow, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger;
        _suppressionWindow = suppressionWindow;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void LogWarning(Exception ex, string messageTemplate, params object[] args)
    {
        var state = _states.GetOrAdd(messageTemplate, _ => new ThrottleState
        {
            WindowStart = DateTimeOffset.MinValue
        });

        lock (state.Lock)
        {
            var now = _clock();
            var elapsed = now - state.WindowStart;

            if (elapsed >= _suppressionWindow)
            {
                // Window expired (or first call) — emit summary if anything was suppressed
                if (state.SuppressedCount > 0)
                {
                    _logger.LogWarning(
                        "Suppressed {SuppressedCount} repeated persistence warning(s) for \"{MessageTemplate}\" over the last {Window}",
                        state.SuppressedCount, messageTemplate, _suppressionWindow);
                }

                // Log the actual warning and start a new window
                _logger.LogWarning(ex, messageTemplate, args);
                state.WindowStart = now;
                state.SuppressedCount = 0;
            }
            else
            {
                // Within window — suppress
                state.SuppressedCount++;
            }
        }
    }

    /// <summary>
    /// Returns the current state for a given message template (for testing).
    /// </summary>
    internal ThrottleState? GetState(string messageTemplate)
    {
        _states.TryGetValue(messageTemplate, out var state);
        return state;
    }
}
