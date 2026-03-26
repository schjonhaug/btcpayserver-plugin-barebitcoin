using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    [Fact]
    public async Task ConcurrentFlushAsync_DoesNotCorruptPersistedFile()
    {
        var writer = new BlockingDiskWriter(new FileDiskWriter(FilePath));
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);

        for (var i = 0; i < 20; i++)
            await service.TrackInvoice($"inv-{i}");

        // Start first flush — it will block inside WriteAsync
        var flush1 = service.FlushAsync();
        await writer.EnteredWrite;

        // Mutate state while first flush is blocked in I/O
        for (var i = 20; i < 30; i++)
            await service.TrackInvoice($"inv-{i}");
        for (var i = 10; i < 15; i++)
            await service.UntrackInvoice($"inv-{i}");

        // Release first flush and let it complete
        writer.Release();
        await flush1;

        // Final flush to persist the mutations
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
    public async Task ConcurrentFlushAsync_VersionCheckPreventsRedundantWrites()
    {
        var writer = new CountingDiskWriter(new FileDiskWriter(FilePath));
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);

        for (var i = 0; i < 10; i++)
            await service.TrackInvoice($"inv-{i}");

        // Launch multiple concurrent flushes — only one should actually write
        var tasks = new Task[10];
        for (var i = 0; i < 10; i++)
            tasks[i] = service.FlushAsync();
        await Task.WhenAll(tasks);

        // Version-based deduplication means at most one write occurred
        Assert.Equal(1, writer.WriteCount);

        // Data integrity check
        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();
        Assert.Equal(10, tracked.Count);
    }

    [Fact]
    public async Task FailedDiskWrite_RemarksAsDirtyAndRetriesSuccessfully()
    {
        var writer = new FailOnceWriter(new FileDiskWriter(FilePath));
        await using var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);
        await service.TrackInvoice("inv-1");

        // First flush fails due to simulated I/O error
        await service.FlushAsync();
        Assert.False(File.Exists(FilePath));

        // Second flush succeeds — service re-marked dirty after failure
        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();
        Assert.Contains("inv-1", tracked);
    }

    [Fact]
    public async Task DisposeAsync_DuringFlush_DoesNotLoseData()
    {
        var writer = new BlockingDiskWriter(new FileDiskWriter(FilePath));
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath, writer);

        for (var i = 0; i < 10; i++)
            await service.TrackInvoice($"inv-{i}");

        // Start flush — it will block inside WriteAsync
        var flushTask = service.FlushAsync();
        await writer.EnteredWrite;

        // Dispose while flush is blocked in I/O
        var disposeTask = service.DisposeAsync();

        // Release the blocked flush
        writer.Release();
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
    public async Task TimerFlush_DiskFailure_LogsWarningAndServiceRemainsStable()
    {
        var logger = new SignalingRecordingLogger("Failed to flush tracked invoices to disk");
        var writer = new SignalingFailWriter();
        await using var service = new BareBitcoinInvoiceService(logger, FilePath, writer);

        await service.TrackInvoice("inv-1");

        // Wait for the timer callback to complete logging after the disk failure
        var logged = await Task.WhenAny(logger.ExpectedLogEmitted, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(logger.ExpectedLogEmitted, logged);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Failed to flush tracked invoices to disk"));

        // Service remains stable — in-memory state is intact
        var tracked = await service.GetTrackedInvoices();
        Assert.Contains("inv-1", tracked);

        await service.TrackInvoice("inv-2");
        tracked = await service.GetTrackedInvoices();
        Assert.Equal(2, tracked.Count);
    }

    [Fact]
    public async Task TimerCallback_CatchesException_WhenFlushAsyncThrows()
    {
        var logger = new EscalatingThrowLogger();
        var writer = new SignalingFailWriter();
        await using var service = new BareBitcoinInvoiceService(logger, FilePath, writer);
        try
        {
            await service.TrackInvoice("inv-1");

            // Wait for the timer callback's defense-in-depth catch to log the error
            var logged = await Task.WhenAny(logger.ExpectedLogEmitted, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Equal(logger.ExpectedLogEmitted, logged);

            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Error && e.Message.Contains("Unhandled exception in flush timer callback"));

            // Service remains stable
            var tracked = await service.GetTrackedInvoices();
            Assert.Contains("inv-1", tracked);

            await service.TrackInvoice("inv-2");
            tracked = await service.GetTrackedInvoices();
            Assert.Equal(2, tracked.Count);
        }
        finally
        {
            // Disable throwing so dispose's FlushAsync doesn't cause issues
            logger.ThrowOnFlushLogs = false;
        }
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

        // First call logs an error, subsequent calls within the 5-minute window are suppressed.
        var flushErrors = logger.Entries
            .Where(e => e.Level == LogLevel.Error && e.Message.Contains("Failed to flush tracked invoices to disk"))
            .ToList();
        Assert.Single(flushErrors);
    }

    [Fact]
    public async Task FlushAsync_SerializationFailure_IncrementsBackoffAndSetsException()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSerializeService(logger, FilePath);
        await service.TrackInvoice("inv-1");

        await service.FlushAsync();
        Assert.Equal(1, service.ConsecutiveFlushFailures);
        Assert.IsType<InvalidOperationException>(service.LastFlushException);

        await service.FlushAsync();
        Assert.Equal(2, service.ConsecutiveFlushFailures);

        await service.FlushAsync();
        Assert.Equal(3, service.ConsecutiveFlushFailures);
    }

    [Fact]
    public async Task FlushAsync_SerializationRecovery_ResetsBackoffAndClearsException()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSerializeService(logger, FilePath, failCount: 2);
        await service.TrackInvoice("inv-1");

        await service.FlushAsync();
        await service.FlushAsync();
        Assert.Equal(2, service.ConsecutiveFlushFailures);
        Assert.NotNull(service.LastFlushException);

        await service.FlushAsync();
        Assert.Equal(0, service.ConsecutiveFlushFailures);
        Assert.Null(service.LastFlushException);

        var persisted = File.ReadAllText(FilePath);
        Assert.Contains("inv-1", persisted);
    }

    [Fact]
    public async Task FlushAsync_ThrottlesLogs_OnRepeatedSerializationFailure()
    {
        var logger = new RecordingLogger();
        await using var service = new FailingSerializeService(logger, FilePath);
        await service.TrackInvoice("inv-1");

        for (var i = 0; i < 10; i++)
            await service.FlushAsync();

        var serializeErrors = logger.Entries
            .Where(e => e.Level == LogLevel.Error && e.Message.Contains("Failed to serialize tracked invoices"))
            .ToList();
        Assert.Single(serializeErrors);
    }

    private class FailingSerializeService : BareBitcoinInvoiceService
    {
        private int _failCount;

        public FailingSerializeService(ILogger logger, string dataFilePath, int failCount = int.MaxValue)
            : base(logger, dataFilePath)
        {
            _failCount = failCount;
        }

        internal override string SerializeRegistry()
        {
            if (_failCount > 0)
            {
                _failCount--;
                throw new InvalidOperationException("Simulated serialization failure");
            }
            return base.SerializeRegistry();
        }
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

    private class BlockingDiskWriter : IDiskWriter
    {
        private readonly IDiskWriter _inner;
        private readonly TaskCompletionSource _enteredWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingDiskWriter(IDiskWriter inner) => _inner = inner;

        public Task EnteredWrite => _enteredWrite.Task;

        public string? Read() => _inner.Read();

        public async Task WriteAsync(string content)
        {
            _enteredWrite.TrySetResult();
            await _releaseWrite.Task;
            await _inner.WriteAsync(content);
        }

        public void Release() => _releaseWrite.TrySetResult();
    }

    private class CountingDiskWriter : IDiskWriter
    {
        private readonly IDiskWriter _inner;
        private int _writeCount;

        public CountingDiskWriter(IDiskWriter inner) => _inner = inner;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public string? Read() => _inner.Read();

        public async Task WriteAsync(string content)
        {
            Interlocked.Increment(ref _writeCount);
            await _inner.WriteAsync(content);
        }
    }

    private class FailOnceWriter : IDiskWriter
    {
        private readonly IDiskWriter _inner;
        private int _callCount;

        public FailOnceWriter(IDiskWriter inner) => _inner = inner;

        public string? Read() => _inner.Read();

        public async Task WriteAsync(string content)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
                throw new IOException("Simulated disk failure");
            await _inner.WriteAsync(content);
        }
    }

    private class SignalingFailWriter : IDiskWriter
    {
        public string? Read() => null;

        public Task WriteAsync(string content)
        {
            throw new IOException("Simulated disk failure");
        }
    }

    private class SignalingRecordingLogger : ILogger
    {
        private readonly string _signalSubstring;
        private readonly TaskCompletionSource _expectedLog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public SignalingRecordingLogger(string signalSubstring) => _signalSubstring = signalSubstring;

        public Task ExpectedLogEmitted => _expectedLog.Task;
        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToList();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _entries.Enqueue((logLevel, message, exception));
            if (message.Contains(_signalSubstring))
                _expectedLog.TrySetResult();
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private class EscalatingThrowLogger : ILogger
    {
        public volatile bool ThrowOnFlushLogs = true;
        private readonly TaskCompletionSource _expectedLog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public Task ExpectedLogEmitted => _expectedLog.Task;
        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToList();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            if (ThrowOnFlushLogs)
            {
                // Force exceptions to escape FlushAsync's internal catches so the
                // timer callback's defense-in-depth catch is exercised.
                if (logLevel == LogLevel.Warning && message.Contains("Failed to flush"))
                    throw new InvalidOperationException("Logger exploded on warning");

                if (logLevel == LogLevel.Error && message.Contains("Unhandled exception in FlushAsync"))
                    throw new InvalidOperationException("Logger exploded on error");
            }

            _entries.Enqueue((logLevel, message, exception));
            if (message.Contains("Unhandled exception in flush timer callback"))
                _expectedLog.TrySetResult();
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
