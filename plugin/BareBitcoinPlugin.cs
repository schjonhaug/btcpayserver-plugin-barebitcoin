#nullable enable
using System.IO;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.BareBitcoin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.BareBitcoin
{
   
    public class BareBitcoinPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.9" }
            
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "BareBitcoin/LNPaymentMethodSetupTab");
            applicationBuilder.AddSingleton<BareBitcoinInvoiceService>(provider =>
            {
                var dataDir = provider.GetRequiredService<IOptions<DataDirectories>>().Value.DataDir;
                var filePath = Path.Combine(dataDir, "Plugins", "BareBitcoin", "tracked-invoices.json");
                var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<BareBitcoinInvoiceService>();
                return new BareBitcoinInvoiceService(logger, filePath);
            });
            applicationBuilder.AddSingleton<IBareBitcoinInvoiceService>(provider => provider.GetRequiredService<BareBitcoinInvoiceService>());
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BareBitcoinLightningConnectionStringHandler>());
            applicationBuilder.AddSingleton<BareBitcoinLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ISwaggerProvider, BareBitcoinSwaggerProvider>();

            base.Execute(applicationBuilder);
        }
        
    }
}
