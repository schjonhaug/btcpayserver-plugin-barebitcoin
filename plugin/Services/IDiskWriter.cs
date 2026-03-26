#nullable enable
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public interface IDiskWriter
{
    string? Read();
    Task WriteAsync(string content);
}
