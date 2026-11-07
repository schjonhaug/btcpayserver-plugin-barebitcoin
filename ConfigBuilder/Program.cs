using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

var plugins = Directory.GetDirectories("../../../../Plugins");
var p = "";
foreach (var plugin in plugins)
{
    try
    {
        var assemblyConfigurationAttribute = typeof(Program).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
        var buildConfigurationName = assemblyConfigurationAttribute?.Configuration;
        var pluginBinDir = Path.Combine(Path.GetFullPath(plugin), "bin");
        var pluginDllName = $"{Path.GetFileName(plugin)}.dll";
        var f = Path.Combine(pluginBinDir, buildConfigurationName ?? "Debug", "net10.0", pluginDllName);
        if (File.Exists(f))
            p += $"{f};";
        else
        {
            f = Path.Combine(pluginBinDir, "Debug", "net10.0", pluginDllName);
            if (File.Exists(f))
                p += $"{f};";
        }
        
        // if (x.Any(s => s.EndsWith("Altcoins-Debug")))
        // {
        //     p += $"{Path.GetFullPath(plugin)}/bin/Altcoins-Debug/net10.0/{Path.GetFileName(plugin)}.dll;";
        // }
        // else
        // {
        // }
    }
    catch (Exception e)
    {
        Console.WriteLine(e);
    }
}

var content = JsonSerializer.Serialize(new
{
    DEBUG_PLUGINS = p
});

Console.WriteLine(content);
var btcpayRootCandidates = new[]
{
    Path.GetFullPath("../../../../btcpayserver/BTCPayServer"),
    Path.GetFullPath("../../../../submodules/btcpayserver/BTCPayServer")
};

var appSettingsPath = btcpayRootCandidates
    .FirstOrDefault(path => File.Exists(Path.Combine(path, "BTCPayServer.csproj")));

if (appSettingsPath is null)
{
    throw new DirectoryNotFoundException("Could not locate a BTCPayServer checkout in either the adjacent or submodule layout.");
}

var destination = Path.Combine(appSettingsPath, "appsettings.dev.json");
var settings = File.Exists(destination)
    ? JsonNode.Parse(await File.ReadAllTextAsync(destination)) as JsonObject
        ?? throw new InvalidDataException("Existing appsettings.dev.json must be a JSON object")
    : new JsonObject();
settings["DEBUG_PLUGINS"] = p;
var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    await File.WriteAllTextAsync(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.Move(temporary, destination, overwrite: true);
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}
