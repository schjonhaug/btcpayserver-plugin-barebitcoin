#nullable enable
using System.IO;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class FileDiskWriter : IDiskWriter
{
    private readonly string _filePath;

    public FileDiskWriter(string filePath)
    {
        _filePath = filePath;
    }

    public string? Read()
    {
        if (!File.Exists(_filePath))
            return null;
        return File.ReadAllText(_filePath);
    }

    public async Task WriteAsync(string content)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        var tmpPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
