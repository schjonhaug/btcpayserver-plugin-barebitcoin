#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Opaque, stable ownership boundary for tracked Bare Bitcoin invoices.
/// </summary>
public readonly record struct BareBitcoinInvoiceScope(string Value)
{
    internal static BareBitcoinInvoiceScope ForStoreConnection(
        Uri apiEndpoint,
        Network network,
        string accountId,
        string storeId)
    {
        ArgumentNullException.ThrowIfNull(apiEndpoint);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        var endpoint = apiEndpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var networkName = network.Name;
        var normalizedAccountId = accountId.Trim();
        var normalizedStoreId = storeId.Trim();
        var input = string.Concat(
            Encoding.UTF8.GetByteCount(endpoint), ":", endpoint,
            Encoding.UTF8.GetByteCount(networkName), ":", networkName,
            Encoding.UTF8.GetByteCount(normalizedAccountId), ":", normalizedAccountId,
            Encoding.UTF8.GetByteCount(normalizedStoreId), ":", normalizedStoreId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new BareBitcoinInvoiceScope(Convert.ToHexStringLower(hash));
    }
}
