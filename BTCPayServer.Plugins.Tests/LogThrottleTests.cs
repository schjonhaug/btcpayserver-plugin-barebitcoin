using System;
using System.Collections.Generic;
using System.IO;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class LogThrottleTests
{
    private readonly RecordingLogger _logger = new();
    private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TimeSpan _window = TimeSpan.FromMinutes(5);

    private LogThrottle CreateThrottle() => new(_logger, _window, () => _now);

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
        _now += TimeSpan.FromSeconds(30);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");
        _now += TimeSpan.FromSeconds(30);
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
        _now += TimeSpan.FromSeconds(30);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");
        _now += TimeSpan.FromSeconds(30);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Advance past the window
        _now += TimeSpan.FromMinutes(5);
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
        _now += TimeSpan.FromSeconds(30);
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
        _now += TimeSpan.FromMinutes(6);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");

        // Should have: initial warning, new warning (no summary since nothing was suppressed)
        Assert.Equal(2, _logger.Entries.Count);
        Assert.DoesNotContain("Suppressed", _logger.Entries[1].Message);
    }

    [Fact]
    public void BackwardClockJump_ResetsWindow()
    {
        var throttle = CreateThrottle();

        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-1");
        _now += TimeSpan.FromSeconds(30);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-2");

        // Clock jumps backward by 2 minutes
        _now -= TimeSpan.FromMinutes(2);
        throttle.LogWarning(new IOException("disk"), "Template {Id}", "inv-3");

        // Should have: initial warning, summary of 1 suppressed, new warning after clock jump
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Contains("Template inv-1", _logger.Entries[0].Message);
        Assert.Contains("Suppressed 1", _logger.Entries[1].Message);
        Assert.Contains("Template inv-3", _logger.Entries[2].Message);
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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry
            {
                Level = logLevel,
                Message = formatter(state, exception),
                Exception = exception
            });
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    internal class LogEntry
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
    }
}
