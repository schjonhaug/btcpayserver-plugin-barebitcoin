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
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
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
