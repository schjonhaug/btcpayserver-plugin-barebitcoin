using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BareBitcoin.Services;
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
        await Task.WhenAll(tasks);

        await service.FlushAsync();

        await using var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        for (var i = 0; i < 30; i++)
            Assert.Contains($"inv-{i}", tracked);
        Assert.Equal(30, tracked.Count);
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
}
