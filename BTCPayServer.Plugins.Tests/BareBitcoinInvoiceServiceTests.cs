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

        var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
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

        var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
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

        // Dispose without waiting for debounce timer — should flush
        await service.DisposeAsync();

        var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
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
}
