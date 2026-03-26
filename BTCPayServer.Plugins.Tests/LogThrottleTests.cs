using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        var state = throttle.GetState("Template {Id}");
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
    public void LogError_ThrottlesSameTemplateAcrossLevels()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-2");

        // Second call uses same template, so it's suppressed even though level differs
        Assert.Single(_logger.Entries);
    }

    [Fact]
    public void Log_SummaryUsesHighestSeveritySeenDuringWindow()
    {
        var throttle = CreateThrottle();

        // First call at Warning
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        // Suppress an Error within the window
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-2");

        // Advance past the window and trigger reset at Warning level
        Advance(TimeSpan.FromMinutes(5));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Summary (entry[1]) should be at Error level (the highest severity suppressed),
        // not Warning (the caller's level)
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Equal(LogLevel.Error, _logger.Entries[1].Level);
        Assert.Contains("Suppressed 1", _logger.Entries[1].Message);
    }

    [Fact]
    public void Log_SummaryUsesCallerLevelWhenNoHigherSeveritySuppressed()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        Advance(TimeSpan.FromSeconds(30));
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");

        // Advance past the window and log at Error level
        Advance(TimeSpan.FromMinutes(5));
        throttle.LogError(new IOException("disk"), "Template {Id}", "inv-3");

        // Summary should be at Error (caller's level is higher than suppressed Warning)
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Equal(LogLevel.Error, _logger.Entries[1].Level);
        Assert.Contains("Suppressed 1", _logger.Entries[1].Message);
    }

    [Fact]
    public void DisabledLogLevel_SkipsThrottleEntirely()
    {
        _logger.EnabledLevel = LogLevel.Error;
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");

        Assert.Empty(_logger.Entries);
        Assert.Null(throttle.GetState("Template {Id}"));
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
        public List<LogEntry> Entries { get; } = new();
        public LogLevel EnabledLevel { get; set; } = LogLevel.Trace;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry
            {
                Level = logLevel,
                Message = formatter(state, exception),
                Exception = exception
            });
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
