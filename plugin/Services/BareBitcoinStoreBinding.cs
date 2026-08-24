#nullable enable
using System;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public interface IBareBitcoinStoreBinding
{
    string Protect(string storeId);
    bool IsValid(string storeId, string protectedStoreId);
}

/// <summary>
/// Creates a server-authenticated binding between a Bare Bitcoin connection and its owning BTCPay store.
/// The protected value can be persisted in the connection string, but cannot be minted for another store
/// without access to this BTCPay Server instance's data-protection keys.
/// </summary>
public sealed class BareBitcoinStoreBinding : IBareBitcoinStoreBinding
{
    private readonly IDataProtector _protector;

    public BareBitcoinStoreBinding(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(
            "BTCPayServer.Plugins.BareBitcoin.StoreBinding.v1");
    }

    public string Protect(string storeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        return _protector.Protect(storeId.Trim());
    }

    public bool IsValid(string storeId, string protectedStoreId)
    {
        if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(protectedStoreId))
            return false;

        try
        {
            return StringComparer.Ordinal.Equals(
                _protector.Unprotect(protectedStoreId.Trim()),
                storeId.Trim());
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
