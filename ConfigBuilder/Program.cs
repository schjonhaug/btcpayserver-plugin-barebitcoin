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
        var f = $"{Path.GetFullPath(plugin)}/bin/{buildConfigurationName}/net10.0/{Path.GetFileName(plugin)}.dll";
        if (File.Exists(f))
            p += $"{f};";
        else
        {
            
            f = $"{Path.GetFullPath(plugin)}/bin/Debug/net10.0/{Path.GetFileName(plugin)}.dll";
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
    "../../../../btcpayserver/BTCPayServer/appsettings.dev.json",
    "../../../../submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
};

var appSettingsPath = btcpayRootCandidates
    .Select(Path.GetFullPath)
    .FirstOrDefault(File.Exists) ?? Path.GetFullPath(btcpayRootCandidates[0]);

await File.WriteAllTextAsync(appSettingsPath, content);
