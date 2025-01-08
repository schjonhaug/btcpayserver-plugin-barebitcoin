using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.BareBitcoin;

public class BareBitcoinSwaggerProvider : ISwaggerProvider
{
    private readonly IFileProvider _fileProvider;

    public BareBitcoinSwaggerProvider(IWebHostEnvironment webHostEnvironment)
    {
        _fileProvider = webHostEnvironment.WebRootFileProvider;
    }

    public async Task<JObject> Fetch()
    {
        JObject json = new();
        var fi = _fileProvider.GetFileInfo("Resources/swagger/v1/openapi.json");
        using var reader = new System.IO.StreamReader(fi.CreateReadStream());
        json.Merge(JObject.Parse(await reader.ReadToEndAsync()));
        return json;
    }
} 