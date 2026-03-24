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
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-2");

        // Simulate restart by creating a new instance with the same file
        var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        Assert.Contains("inv-1", tracked);
        Assert.Contains("inv-2", tracked);
        Assert.Equal(2, tracked.Count);
    }

    [Fact]
    public async Task UntrackInvoice_RemovesFromFile()
    {
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-2");
        await service.UntrackInvoice("inv-1");

        var service2 = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service2.GetTrackedInvoices();

        Assert.DoesNotContain("inv-1", tracked);
        Assert.Contains("inv-2", tracked);
    }

    [Fact]
    public async Task MissingFile_StartsEmpty()
    {
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service.GetTrackedInvoices();

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task CorruptFile_StartsEmpty()
    {
        File.WriteAllText(FilePath, "not valid json {{{");

        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        var tracked = await service.GetTrackedInvoices();

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task TrackInvoice_IsDeduplicated()
    {
        var service = new BareBitcoinInvoiceService(NullLogger.Instance, FilePath);
        await service.TrackInvoice("inv-1");
        await service.TrackInvoice("inv-1");

        var tracked = await service.GetTrackedInvoices();
        Assert.Single(tracked);
    }
}
