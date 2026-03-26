#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Throttles repeated log messages by message template, logging the first occurrence
/// immediately and suppressing duplicates for a configurable window.
/// </summary>
internal class LogThrottle
{
    private readonly ILogger _logger;
    private readonly TimeSpan _suppressionWindow;
    private readonly Func<long> _clock;
    private readonly int _maxEntries;
    private readonly object _evictionLock = new();
    private readonly ConcurrentDictionary<string, ThrottleState> _states = new();

    internal class ThrottleState
    {
        public long WindowStart;
        public bool IsFirstCall = true;
        public int SuppressedCount;
        public readonly object Lock = new();
    }

    public LogThrottle(ILogger logger, TimeSpan suppressionWindow, Func<long>? clock = null, int maxEntries = 100)
    {
        if (suppressionWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(suppressionWindow));
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _logger = logger;
        _suppressionWindow = suppressionWindow;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Logs a message at the specified level, throttling repeated calls with the same
    /// <paramref name="messageTemplate"/>. Prefer static string literals for templates.
    /// A hard cap evicts the oldest entry when the number of tracked templates exceeds
    /// <c>maxEntries</c>.
    /// </summary>
    public void Log(LogLevel logLevel, Exception? ex, string messageTemplate, params object[] args)
    {
        if (!_logger.IsEnabled(logLevel)) return;

        ThrottleState state;

        // Fast path: template already tracked — no eviction needed
        if (_states.TryGetValue(messageTemplate, out state!))
            goto throttle;

        // Slow path: new template — serialize eviction + add to enforce cap
        lock (_evictionLock)
        {
            // Re-check after acquiring lock (another thread may have added it)
            if (!_states.TryGetValue(messageTemplate, out state!))
            {
                if (_states.Count >= _maxEntries)
                {
                    string? oldestKey = null;
                    long oldestWindow = long.MaxValue;
                    foreach (var kvp in _states)
                    {
                        if (kvp.Value.WindowStart < oldestWindow)
                        {
                            oldestWindow = kvp.Value.WindowStart;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestKey != null)
                        _states.TryRemove(oldestKey, out _);
                }

                state = new ThrottleState { WindowStart = 0 };
                _states[messageTemplate] = state;
            }
        }

        throttle:

        lock (state.Lock)
        {
            var now = _clock();
            var elapsed = Stopwatch.GetElapsedTime(state.WindowStart, now);

            if (state.IsFirstCall || elapsed >= _suppressionWindow)
            {
                // Window expired (or first call) — emit summary if anything was suppressed
                if (state.SuppressedCount > 0)
                {
                    _logger.Log(logLevel,
                        "Suppressed {SuppressedCount} repeated message(s) for \"{MessageTemplate}\" over the last {Window}",
                        state.SuppressedCount, messageTemplate, _suppressionWindow);
                }

                // Log the actual message and start a new window
                _logger.Log(logLevel, ex, messageTemplate, args);
                state.WindowStart = now;
                state.IsFirstCall = false;
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
    /// Logs a warning, throttling repeated calls with the same <paramref name="messageTemplate"/>.
    /// </summary>
    public void LogWarning(Exception? ex, string messageTemplate, params object[] args)
        => Log(LogLevel.Warning, ex, messageTemplate, args);

    /// <summary>
    /// Logs an error, throttling repeated calls with the same <paramref name="messageTemplate"/>.
    /// </summary>
    public void LogError(Exception? ex, string messageTemplate, params object[] args)
        => Log(LogLevel.Error, ex, messageTemplate, args);

    /// <summary>
    /// Returns the current state for a given message template (for testing).
    /// </summary>
    internal ThrottleState? GetState(string messageTemplate)
    {
        _states.TryGetValue(messageTemplate, out var state);
        return state;
    }
}
