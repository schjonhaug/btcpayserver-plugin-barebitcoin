using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class LogThrottleTests
{
    private readonly RecordingLogger _logger = new();
    private long _now = Stopwatch.GetTimestamp();
    private readonly TimeSpan _window = TimeSpan.FromMinutes(5);

    private LogThrottle CreateThrottle() => new(_logger, _window, () => _now);

    private void Advance(TimeSpan duration) =>
        _now += (long)(duration.TotalSeconds * Stopwatch.Frequency);

    [Fact]
    public void FirstCall_LogsImmediately()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");

        Assert.Single(_logger.Entries);
        Assert.Contains("Template inv-1", _logger.Entries[0].Message);
    }

    [Fact]
    public void FirstCall_PassesExceptionToLogger()
    {
        var throttle = CreateThrottle();
        var ex = new IOException("disk full");

        throttle.LogWarning(ex, "Template {Id}", "inv-1");

        Assert.Single(_logger.Entries);
        Assert.Same(ex, _logger.Entries[0].Exception);
        Assert.Equal(LogLevel.Warning, _logger.Entries[0].Level);
    }

    [Fact]
    public void SubsequentCallsWithinWindow_AreSuppressed()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Only the first call should have logged
        Assert.Single(_logger.Entries);

        var state = throttle.GetState(LogLevel.Warning, "Template {Id}");
        Assert.NotNull(state);
        Assert.Equal(2, state!.SuppressedCount);
    }

    [Fact]
    public void AfterWindowExpires_LogsSummaryAndNewWarning()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Advance past the window
        Advance(TimeSpan.FromMinutes(5));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-4");

        // Should have: initial warning, summary, new warning
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Contains("Suppressed 2", _logger.Entries[1].Message);
        Assert.Contains("Template inv-4", _logger.Entries[2].Message);
    }

    [Fact]
    public void DifferentTemplates_ThrottledIndependently()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template A {Id}", "inv-1");
        throttle.LogWarning(new IOException("disk"), "Template B {Id}", "inv-2");

        // Both should log immediately since they're different templates
        Assert.Equal(2, _logger.Entries.Count);

        // Suppress further calls for both
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template A {Id}", "inv-3");
        throttle.LogWarning(new IOException("disk"), "Template B {Id}", "inv-4");

        // Still only 2 logged entries
        Assert.Equal(2, _logger.Entries.Count);
    }

    [Fact]
    public void NoSuppressedCalls_NoSummaryOnNextWindow()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");

        // Advance past window with no intermediate calls
        Advance(TimeSpan.FromMinutes(6));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");

        // Should have: initial warning, new warning (no summary since nothing was suppressed)
        Assert.Equal(2, _logger.Entries.Count);
        Assert.DoesNotContain("Suppressed", _logger.Entries[1].Message);
    }

    [Fact]
    public void LogWarning_WithNullException_LogsSuccessfully()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(null, "Template {Id}", "inv-1");

        Assert.Single(_logger.Entries);
        Assert.Null(_logger.Entries[0].Exception);
        Assert.Equal(LogLevel.Warning, _logger.Entries[0].Level);
    }

    [Fact]
    public void LogError_LogsAtErrorLevel()
    {
        var throttle = CreateThrottle();
        var ex = new IOException("disk full");

        throttle.LogError(ex, "Template {Id}", "inv-1");

        Assert.Single(_logger.Entries);
        Assert.Same(ex, _logger.Entries[0].Exception);
        Assert.Equal(LogLevel.Error, _logger.Entries[0].Level);
    }

    [Fact]
    public void SameTemplate_DifferentLevels_ThrottleIndependently()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-2");

        // Both should log because they are at different levels
        Assert.Equal(2, _logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, _logger.Entries[0].Level);
        Assert.Equal(LogLevel.Error, _logger.Entries[1].Level);
    }

    [Fact]
    public void SameTemplate_DifferentLevels_SuppressedCountsAreIsolated()
    {
        var throttle = CreateThrottle();

        // First calls at each level — both log immediately
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-2");

        // Suppress further calls at each level within the window
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-4");
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-5");

        // Advance past window and trigger both levels again
        Advance(TimeSpan.FromMinutes(5));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-6");
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-7");

        // Expected entries:
        // 0: Warning inv-1 (first call)
        // 1: Error inv-2 (first call)
        // 2: Warning summary "Suppressed 2"
        // 3: Warning inv-6
        // 4: Error summary "Suppressed 1"
        // 5: Error inv-7
        Assert.Equal(6, _logger.Entries.Count);

        Assert.Equal(LogLevel.Warning, _logger.Entries[2].Level);
        Assert.Contains("Suppressed 2", _logger.Entries[2].Message);

        Assert.Equal(LogLevel.Error, _logger.Entries[4].Level);
        Assert.Contains("Suppressed 1", _logger.Entries[4].Message);
    }

    [Fact]
    public void Log_SummaryUsesCallerLogLevel()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");

        // Advance past the window and log at Warning level again
        Advance(TimeSpan.FromMinutes(5));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Summary (entry[1]) should be at Warning level (the caller's level)
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, _logger.Entries[1].Level);
        Assert.Contains("Suppressed 1", _logger.Entries[1].Message);
    }

    [Fact]
    public void DisabledLogLevel_SkipsThrottleEntirely()
    {
        _logger.EnabledLevel = LogLevel.Error;
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");

        Assert.Empty(_logger.Entries);
        Assert.Null(throttle.GetState(LogLevel.Warning, "Template {Id}"));
    }

    [Fact]
    public void FirstCall_LogsImmediately_EvenWithLowTimestamp()
    {
        // Simulate early boot: timestamp near zero, less than the suppression window
        long earlyBootNow = (long)(30 * Stopwatch.Frequency); // 30 seconds after boot
        var throttle = new LogThrottle(_logger, _window, () => earlyBootNow);

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");

        Assert.Single(_logger.Entries);
        Assert.Contains("Template inv-1", _logger.Entries[0].Message);
    }

    [Fact]
    public void StaleEntries_AreEvictedWhenThresholdExceeded()
    {
        var throttle = CreateThrottle();

        // Add 11 distinct templates to exceed the threshold
        for (var i = 0; i < 11; i++)
            throttle.LogWarning(new IOException("disk"), $"Template{i} {{Id}}", "inv");

        Assert.Equal(11, throttle.StateCount);

        // Advance past the suppression window so all entries become stale
        Advance(_window);

        // Next call triggers eviction — stale entries are removed, only the new one remains
        throttle.LogWarning(new IOException("disk"), "Fresh {Id}", "inv");
        Assert.Equal(1, throttle.StateCount);
        Assert.NotNull(throttle.GetState(LogLevel.Warning, "Fresh {Id}"));
    }

    [Fact]
    public void ActiveEntries_AreNotEvicted()
    {
        var throttle = CreateThrottle();

        for (var i = 0; i < 11; i++)
            throttle.LogWarning(new IOException("disk"), $"Template{i} {{Id}}", "inv");

        // Don't advance clock — entries are still within their window
        throttle.LogWarning(new IOException("disk"), "Extra {Id}", "inv");
        Assert.Equal(12, throttle.StateCount);
    }

    [Fact]
    public void Eviction_FlushesSuppressedCountSummary()
    {
        var throttle = CreateThrottle();

        // Create 11 templates, each with a suppressed call
        for (var i = 0; i < 11; i++)
        {
            throttle.LogWarning(new IOException("disk"), $"Template{i} {{Id}}", "inv");
            Advance(TimeSpan.FromSeconds(1));
            throttle.LogWarning(new IOException("disk"), $"Template{i} {{Id}}", "inv"); // suppressed
        }

        _logger.Entries.Clear();

        // Advance past window and trigger eviction
        Advance(_window);
        throttle.LogWarning(new IOException("disk"), "Fresh {Id}", "inv");

        // Each of the 11 stale entries had SuppressedCount=1, so 11 summary logs + 1 fresh warning
        var summaries = _logger.Entries.FindAll(e => e.Message.Contains("Suppressed 1"));
        Assert.Equal(11, summaries.Count);
    }

    [Fact]
    public void BelowThreshold_NoEvictionOccurs()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "A {Id}", "inv");
        throttle.LogWarning(new IOException("disk"), "B {Id}", "inv");
        throttle.LogWarning(new IOException("disk"), "C {Id}", "inv");

        // Advance past window — entries are stale but count (3) is below threshold
        Advance(_window);
        throttle.LogWarning(new IOException("disk"), "A {Id}", "inv");

        // All 3 entries still present — no eviction triggered
        Assert.Equal(3, throttle.StateCount);
    }

    [Fact]
    public void ConcurrentEviction_DoesNotThrowOrCorruptState()
    {
        var logger = new RecordingLogger();
        long now = Stopwatch.GetTimestamp();
        var window = TimeSpan.FromMinutes(5);
        var throttle = new LogThrottle(logger, window, () => Volatile.Read(ref now));

        // Fill past the eviction threshold with stale entries, each with a suppressed call
        for (var i = 0; i < 20; i++)
        {
            throttle.Log(LogLevel.Warning, null, $"Stale{i} {{Id}}", "inv");
            throttle.Log(LogLevel.Warning, null, $"Stale{i} {{Id}}", "inv"); // suppressed
        }

        // Advance past the window so all entries are stale
        Interlocked.Exchange(ref now, now + (long)(window.TotalSeconds * Stopwatch.Frequency));
        logger.Entries.Clear();

        // Hammer eviction from many threads simultaneously
        var barrier = new Barrier(participantCount: 8);
        var tasks = new Task[8];
        for (var t = 0; t < tasks.Length; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < 50; i++)
                    throttle.Log(LogLevel.Warning, null, $"Concurrent{threadId}_{i} {{Id}}", "inv");
            });
        }

        Task.WaitAll(tasks);

        // All tasks completed without exceptions; state is consistent
        Assert.True(throttle.StateCount > 0);
        Assert.True(throttle.StateCount <= 400,
            $"Expected at most 400 entries but found {throttle.StateCount}");

        // Each stale template had SuppressedCount=1, so exactly 20 summary logs should be emitted.
        // No duplicates: extract the template name from each summary and verify uniqueness.
        var summaries = logger.Entries.FindAll(e => e.Message.Contains("Suppressed 1"));
        Assert.Equal(20, summaries.Count);

        var templateNames = summaries.ConvertAll(e =>
        {
            // Extract template name from: Suppressed 1 repeated message(s) for "Stale3 {Id}" ...
            var start = e.Message.IndexOf('"') + 1;
            var end = e.Message.IndexOf('"', start);
            return e.Message.Substring(start, end - start);
        });
        Assert.Equal(20, new HashSet<string>(templateNames).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveWindow(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LogThrottle(_logger, TimeSpan.FromSeconds(seconds)));
    }

    private class RecordingLogger : ILogger
    {
        private readonly object _lock = new();
        public List<LogEntry> Entries { get; } = new();
        public LogLevel EnabledLevel { get; set; } = LogLevel.Trace;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                Entries.Add(new LogEntry
                {
                    Level = logLevel,
                    Message = formatter(state, exception),
                    Exception = exception
                });
            }
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= EnabledLevel;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    internal class LogEntry
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
    }
}
