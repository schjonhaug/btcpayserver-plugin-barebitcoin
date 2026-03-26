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
    private readonly TimeSpan _evictionAge;
    private readonly TimeSpan _evictionInterval;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, ThrottleState> _states = new();
    private long _lastEvictionTimestamp;

    internal class ThrottleState
    {
        public long WindowStart;
        public long LastAccessed;
        public bool IsFirstCall = true;
        public int SuppressedCount;
        public readonly object Lock = new();
    }

    public LogThrottle(ILogger logger, TimeSpan suppressionWindow, Func<long>? clock = null)
    {
        if (suppressionWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(suppressionWindow));
        _logger = logger;
        _suppressionWindow = suppressionWindow;
        _evictionAge = 10 * suppressionWindow;
        _evictionInterval = suppressionWindow;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>
    /// Logs a message at the specified level, throttling repeated calls with the same
    /// <paramref name="messageTemplate"/>. The template must be a static string literal;
    /// dynamic templates cause memory growth until entries are evicted.
    /// </summary>
    public void Log(LogLevel logLevel, Exception? ex, string messageTemplate, params object[] args)
    {
        if (!_logger.IsEnabled(logLevel)) return;

        var now = _clock();

        if (Stopwatch.GetElapsedTime(_lastEvictionTimestamp, now) >= _evictionInterval)
        {
            _lastEvictionTimestamp = now;
            EvictStaleEntries(now);
        }

        var state = _states.GetOrAdd(messageTemplate, _ => new ThrottleState
        {
            WindowStart = 0
        });

        lock (state.Lock)
        {
            state.LastAccessed = now;
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

    /// <summary>
    /// Returns the number of tracked message templates (for testing).
    /// </summary>
    internal int StateCount => _states.Count;

    private void EvictStaleEntries(long now)
    {
        foreach (var kvp in _states)
        {
            if (Stopwatch.GetElapsedTime(kvp.Value.LastAccessed, now) >= _evictionAge)
            {
                _states.TryRemove(kvp.Key, out _);
            }
        }
    }
}
