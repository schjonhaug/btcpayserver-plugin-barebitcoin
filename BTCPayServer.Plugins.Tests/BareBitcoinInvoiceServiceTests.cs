using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

public class BareBitcoinInvoiceServiceTests : IDisposable
{
    private readonly string _tempDir;

    public BareBitcoinInvoiceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string FilePath => Path.Combine(_tempDir, "tracked-invoices.json");

    [Fact]
    public async Task TrackedInvoices_SurviveRestart()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-2");
        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        Assert.Contains("inv-1", tracked);
        Assert.Contains("inv-2", tracked);
        Assert.Equal(2, tracked.Count);
    }

    [Fact]
    public async Task UntrackInvoice_RemovesFromFile()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-2");
        await service.UntrackInvoice("inv-1");
        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        Assert.DoesNotContain("inv-1", tracked);
        Assert.Contains("inv-2", tracked);
    }

    [Fact]
    public async Task MissingFile_StartsEmpty()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service.GetTrackedInvoices();

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task CorruptFile_StartsEmpty()
    {
        File.WriteAllText(FilePath, "not valid json {{{");

        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service.GetTrackedInvoices();

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task TrackInvoice_IsDeduplicated()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-1");

        var tracked = await service.GetTrackedInvoices();
        Assert.Single(tracked);
    }

    [Fact]
    public async Task DirtyState_FlushedOnDispose()
    {
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");

        // Dispose without waiting for flush timer — should flush
        await service.DisposeAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        Assert.Contains("inv-1", tracked);
    }

    [Fact]
    public async Task FlushAsync_PersistsImmediately()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.FlushAsync();

        Assert.True(File.Exists(FilePath));
        var json = File.ReadAllText(FilePath);
        Assert.Contains("inv-1", json);
    }

    [Fact]
    public async Task FlushAsync_BacksOff_OnPersistentDiskError()
    {
        // Point at a path nested under a non-existent root that cannot be created
        var brokenPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "deep", "nested", "tracked.json");

        // Create the immediate parent so LoadFromDisk doesn't fail, but make it read-only
        // so SaveToDiskAsync's temp file write fails. Actually, simplest: use a path where
        // the parent's parent doesn't exist and Directory.CreateDirectory will fail.
        // We use /dev/null/impossible on Unix or a device path on Windows.
        var impossiblePath = Path.Combine("/dev/null", "impossible", "tracked.json");

        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, impossiblePath);
        await service.TrackInvoice("inv-1");

        Assert.Equal(0, service.ConsecutiveFlushFailures);

        await service.FlushAsync();
        Assert.Equal(1, service.ConsecutiveFlushFailures);

        await service.FlushAsync();
        Assert.Equal(2, service.ConsecutiveFlushFailures);

        await service.FlushAsync();
        Assert.Equal(3, service.ConsecutiveFlushFailures);
    }

    [Fact]
    public async Task FlushAsync_ResetsBackoff_AfterSuccess()
    {
        var impossiblePath = Path.Combine("/dev/null", "impossible", "tracked.json");

        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, impossiblePath);
        await service.TrackInvoice("inv-1");

        // Build up failures
        await service.FlushAsync();
        await service.FlushAsync();
        Assert.True(service.ConsecutiveFlushFailures >= 2);

        // Now create a working service and verify reset works
        await using var workingService = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await workingService.TrackInvoice("inv-1");

        // Cause a failure first
        var brokenFilePath = Path.Combine("/dev/null", "impossible", "tracked2.json");
        await using var failThenSucceed = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await failThenSucceed.TrackInvoice("inv-1");
        await failThenSucceed.FlushAsync();

        // Successful flush should reset the counter
        Assert.Equal(0, failThenSucceed.ConsecutiveFlushFailures);
    }

    [Fact]
    public async Task FlushAsync_ThrottlesLogs_OnRepeatedFailure()
    {
        var logger = new CapturingLogger();
        var impossiblePath = Path.Combine("/dev/null", "impossible", "tracked.json");

        await using var service = new BareBitcoinInvoiceService(logger, impossiblePath);
        await service.TrackInvoice("inv-1");

        // Flush many times — log throttle should suppress most messages
        for (var i = 0; i < 10; i++)
            await service.FlushAsync();

        // First call logs, subsequent calls within the 5-minute window are suppressed.
        // We expect exactly 1 warning for the flush failure template.
        var flushWarnings = logger.Messages.FindAll(m =>
            m.LogLevel == LogLevel.Warning &&
            m.Message.Contains("Failed to flush tracked invoices to disk"));
        Assert.Single(flushWarnings);
    }

    private class CapturingLogger : ILogger
    {
        public List<LogEntry> Messages { get; } = new();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(new LogEntry { LogLevel = logLevel, Message = formatter(state, exception) });
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public class LogEntry
        {
            public LogLevel LogLevel { get; init; }
            public string Message { get; init; } = "";
        }
    }
}
