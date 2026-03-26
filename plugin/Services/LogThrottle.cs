#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Throttles repeated log messages by message template, logging the first occurrence
/// immediately and suppressing duplicates for a configurable window.
/// </summary>
internal class LogThrottle
{
    private const int MaxEntries = 10;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="LogThrottle"/> class.
    /// </summary>
    /// <param name="logger">The logger to write throttled messages to.</param>
    /// <param name="suppressionWindow">How long to suppress duplicate messages after the first occurrence.</param>
    /// <param name="clock">
    /// Optional clock function that must return values compatible with
    /// <see cref="Stopwatch.GetTimestamp"/>. Defaults to <see cref="Stopwatch.GetTimestamp"/>.
    /// </param>
    public LogThrottle(ILogger logger, TimeSpan suppressionWindow, Func<long>? clock = null)
    {
        if (suppressionWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(suppressionWindow));
        _logger = logger;
        _suppressionWindow = suppressionWindow;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>
    /// Logs a message at the specified level, throttling repeated calls with the same
    /// <paramref name="messageTemplate"/>. The template should be a static string literal.
    /// Dynamic templates are tolerated—stale entries are evicted—but active-window growth
    /// is unbounded; prefer static templates.
    /// </summary>
    public void Log(LogLevel logLevel, Exception? ex, string messageTemplate, params object[] args)
    {
        if (!_logger.IsEnabled(logLevel)) return;

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

        if (_states.Count > MaxEntries)
            EvictStaleEntries();
    }

    private void EvictStaleEntries()
    {
        var now = _clock();
        foreach (var kvp in _states)
        {
            var state = kvp.Value;
            lock (state.Lock)
            {
                if (state.IsFirstCall)
                    continue;

                var elapsed = Stopwatch.GetElapsedTime(state.WindowStart, now);
                if (elapsed >= _suppressionWindow)
                {
                    // Remove only if the value still matches the instance we checked,
                    // avoiding accidental removal of a concurrently recreated entry.
                    // Only the thread that successfully removes the entry emits the summary,
                    // preventing duplicate logs under concurrent eviction.
                    if (!((ICollection<KeyValuePair<string, ThrottleState>>)_states).Remove(kvp))
                        continue;

                    if (state.SuppressedCount > 0)
                    {
                        _logger.LogWarning(
                            "Suppressed {SuppressedCount} repeated message(s) for \"{MessageTemplate}\" over the last {Window}",
                            state.SuppressedCount, kvp.Key, _suppressionWindow);
                    }
                }
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
}
