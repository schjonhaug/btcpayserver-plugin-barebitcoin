using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public async Task FlushAsync_Failure_SetsLastFlushException()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath);
        await service.TrackInvoice("inv-1");
        await service.FlushAsync();

        Assert.NotNull(service.LastFlushException);
        Assert.IsType<IOException>(service.LastFlushException);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task FlushAsync_Success_ClearsLastFlushException()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath, failCount: 1);
        await service.TrackInvoice("inv-1");

        await service.FlushAsync(); // fails
        Assert.NotNull(service.LastFlushException);

        await service.FlushAsync(); // succeeds
        Assert.Null(service.LastFlushException);
    }

    [Fact]
    public async Task DisposeAsync_FlushFailure_RetriesAndLogsError()
    {
        var logger = new RecordingLogger();
        var service = new FailingSaveService(logger, FilePath);
        await service.TrackInvoice("inv-1");

        await service.DisposeAsync();

        Assert.NotNull(service.LastFlushException);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Retrying"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("Final flush failed"));
    }

    [Fact]
    public async Task DisposeAsync_FlushFailure_RetrySucceeds()
    {
        var logger = new RecordingLogger();
        var service = new FailingSaveService(logger, FilePath, failCount: 1);
        await service.TrackInvoice("inv-1");

        await service.DisposeAsync();

        Assert.Null(service.LastFlushException);
        Assert.True(File.Exists(FilePath));
        var json = File.ReadAllText(FilePath);
        Assert.Contains("inv-1", json);
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("Final flush failed"));
    }

    [Fact]
    public async Task ConcurrentFlushAsync_DoesNotCorruptPersistedFile()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);

        for (var i = 0; i < 20; i++)
            await service.TrackInvoice($"inv-{i}");

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
            tasks.Add(service.FlushAsync());
        for (var i = 20; i < 30; i++)
            tasks.Add(service.TrackInvoice($"inv-{i}"));
        // Untrack some of the initial invoices concurrently
        for (var i = 10; i < 15; i++)
            tasks.Add(service.UntrackInvoice($"inv-{i}"));
        await Task.WhenAll(tasks);

        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        // inv-0..9 tracked, inv-10..14 untracked, inv-15..29 tracked
        for (var i = 0; i < 10; i++)
            Assert.Contains($"inv-{i}", tracked);
        for (var i = 10; i < 15; i++)
            Assert.DoesNotContain($"inv-{i}", tracked);
        for (var i = 15; i < 30; i++)
            Assert.Contains($"inv-{i}", tracked);
        Assert.Equal(25, tracked.Count);
    }

    [Fact]
    public async Task FailedDiskWrite_RemarksAsDirtyAndRetriesSuccessfully()
    {
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");

        var tmpPath = FilePath + ".tmp";
        Directory.CreateDirectory(tmpPath);

        try
        {
            await service.FlushAsync();
            Assert.False(File.Exists(FilePath));
        }
        finally
        {
            Directory.Delete(tmpPath, recursive: true);
        }

        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();
        Assert.Contains("inv-1", tracked);
    }

    [Fact]
    public async Task DisposeAsync_DuringFlush_DoesNotLoseData()
    {
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);

        for (var i = 0; i < 10; i++)
            await service.TrackInvoice($"inv-{i}");

        var flushTask = service.FlushAsync();
        var disposeTask = service.DisposeAsync();

        await flushTask;
        await disposeTask;

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        for (var i = 0; i < 10; i++)
            Assert.Contains($"inv-{i}", tracked);
        Assert.Equal(10, tracked.Count);
    }

    [Fact]
    public async Task FlushAsync_BacksOff_OnPersistentDiskError()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath);
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
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath, failCount: 2);
        await service.TrackInvoice("inv-1");

        // Build up failures
        await service.FlushAsync();
        await service.FlushAsync();
        Assert.Equal(2, service.ConsecutiveFlushFailures);

        // Successful flush should reset the counter
        await service.FlushAsync();
        Assert.Equal(0, service.ConsecutiveFlushFailures);
    }

    [Fact]
    public async Task GetFlushBackoff_ProducesExponentialDelaysCappedAtMax()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath);
        await service.TrackInvoice("inv-1");

        // Before any failure, backoff starts at FlushInterval
        // (failures=0 means GetFlushBackoff uses exponent -1 → 0.5s, but this path
        // is never hit in practice since increment always precedes the call)

        // After 1st failure → exponent 0 → 1s
        await service.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), service.GetFlushBackoff());

        // After 2nd failure → exponent 1 → 2s
        await service.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), service.GetFlushBackoff());

        // After 3rd failure → exponent 2 → 4s
        await service.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(4), service.GetFlushBackoff());

        // After 4th failure → exponent 3 → 8s
        await service.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(8), service.GetFlushBackoff());

        // After 5th failure → exponent 4 → 16s
        await service.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(16), service.GetFlushBackoff());

        // After 6th failure → exponent 5 → 32 → capped at 30s
        await service.FlushAsync();
        Assert.Equal(BareBitcoinInvoiceService.MaxFlushBackoff, service.GetFlushBackoff());
    }

    [Fact]
    public async Task FlushAsync_ThrottlesLogs_OnRepeatedFailure()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSaveService(logger, FilePath);
        await service.TrackInvoice("inv-1");

        // Flush many times — log throttle should suppress most messages
        for (var i = 0; i < 10; i++)
            await service.FlushAsync();

        // First call logs a warning, subsequent calls within the 5-minute window are suppressed.
        var flushWarnings = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Failed to flush tracked invoices to disk"))
            .ToList();
        Assert.Single(flushWarnings);
    }

    private class FailingSaveService : BareBitcoinInvoiceService
    {
        private int _failCount;

        public FailingSaveService(ILogger logger, string dataFilePath, int failCount = int.MaxValue)
            : base(logger, dataFilePath)
        {
            _failCount = failCount;
        }

        internal override async Task SaveToDiskAsync(string json)
        {
            if (_failCount > 0)
            {
                _failCount--;
                throw new IOException("Simulated disk failure");
            }
            await base.SaveToDiskAsync(json);
        }
    }

    private class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToList();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
