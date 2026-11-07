#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class FileDiskWriter : IDiskWriter
{
    private readonly string _filePath;

    public FileDiskWriter(string filePath) => _filePath = filePath;

    public string? Read()
    {
        // File.Exists masks access failures as absence. Only a genuinely missing file
        // is a new registry; all other errors must reach the fail-safe loader.
        try { return File.ReadAllText(_filePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task WriteAsync(string content)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
