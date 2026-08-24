#nullable enable
using BTCPayServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public interface IBareBitcoinStoreContext
{
    string? GetCurrentStoreId();
}

/// <summary>
/// Reads the store that BTCPay authenticated for the current request. Lightning connection handlers
/// are singletons, so the HTTP accessor is resolved from a short-lived scope instead of being captured.
/// Background listener recreation has no HTTP context and is authenticated by the persisted store binding.
/// </summary>
public sealed class BareBitcoinStoreContext(IServiceScopeFactory scopeFactory) : IBareBitcoinStoreContext
{
    public string? GetCurrentStoreId()
    {
        using var scope = scopeFactory.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        return accessor.HttpContext?.GetCurrentStoreId();
    }
}
