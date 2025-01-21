#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BareBitcoin
{
   
    public class BareBitcoinPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
            
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "BareBitcoin/LNPaymentMethodSetupTab");
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BareBitcoinLightningConnectionStringHandler>());
            applicationBuilder.AddSingleton<BareBitcoinLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ISwaggerProvider, BareBitcoinSwaggerProvider>();
            applicationBuilder.AddHttpContextAccessor();

            base.Execute(applicationBuilder);
        }
        
    }
}