using System.Reflection;
using System.Text.Json;

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

await File.WriteAllTextAsync(Path.Combine(appSettingsPath, "appsettings.dev.json"), content);
