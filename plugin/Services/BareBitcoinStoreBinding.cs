#nullable enable
using System;
using System.Security.Cryptography;
using BTCPayServer.Lightning;
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

    /// <summary>
    /// Returns the binding to put in the store's Lightning setup. A still-valid binding from the existing
    /// connection string is kept: data protection output is randomized, and a new binding would change the
    /// connection string on every save, making BTCPay start a second listener for the same store.
    /// </summary>
    public static string ForSetup(IBareBitcoinStoreBinding binding, string storeId, string? existingConnectionString)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        if (!string.IsNullOrWhiteSpace(existingConnectionString))
        {
            try
            {
                var values = LightningConnectionStringHelper.ExtractValues(existingConnectionString, out var type);
                if (type == "barebitcoin" &&
                    values.TryGetValue("store-binding", out var existingBinding) &&
                    binding.IsValid(storeId, existingBinding))
                {
                    return existingBinding.Trim();
                }
            }
            catch (FormatException)
            {
            }
        }

        return binding.Protect(storeId);
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
