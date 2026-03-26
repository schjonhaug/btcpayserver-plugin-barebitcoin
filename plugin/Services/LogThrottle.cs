#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, ThrottleState> _states = new();

    internal class ThrottleState
    {
        public long WindowStart;
        public bool IsFirstCall = true;
        public int SuppressedCount;
        public readonly object Lock = new();
    }

    public LogThrottle(ILogger logger, TimeSpan suppressionWindow, Func<long>? clock = null)
    {
        if (suppressionWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(suppressionWindow));
        _logger = logger;
        _suppressionWindow = suppressionWindow;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>
    /// Logs a warning, throttling repeated calls with the same <paramref name="messageTemplate"/>.
    /// The template must be a static string literal; dynamic templates will cause unbounded memory growth.
    /// </summary>
    public void LogWarning(Exception ex, string messageTemplate, params object[] args)
    {
        var state = _states.GetOrAdd(messageTemplate, _ => new ThrottleState
        {
            WindowStart = 0
        });

        lock (state.Lock)
        {
            var now = _clock();
            var elapsed = Stopwatch.GetElapsedTime(state.WindowStart, now);

            if (state.IsFirstCall || elapsed >= _suppressionWindow)
            {
                // Window expired (or first call) — emit summary if anything was suppressed
                if (state.SuppressedCount > 0)
                {
                    _logger.LogWarning(
                        "Suppressed {SuppressedCount} repeated warning(s) for \"{MessageTemplate}\" over the last {Window}",
                        state.SuppressedCount, messageTemplate, _suppressionWindow);
                }

                // Log the actual warning and start a new window
                _logger.LogWarning(ex, messageTemplate, args);
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
    /// Returns the current state for a given message template (for testing).
    /// </summary>
    internal ThrottleState? GetState(string messageTemplate)
    {
        _states.TryGetValue(messageTemplate, out var state);
        return state;
    }
}
