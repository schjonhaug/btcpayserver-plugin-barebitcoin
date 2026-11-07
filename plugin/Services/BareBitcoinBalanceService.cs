#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>Per-client, account-keyed cache. Unavailable balances are never reported as zero.</summary>
public class BareBitcoinBalanceService
{
    private readonly BareBitcoinApiService _apiService;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _balanceLock = new(1, 1);
    private readonly Dictionary<string, (LightningNodeBalance balance, long timestamp)> _cache = new(StringComparer.Ordinal);
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    public BareBitcoinBalanceService(BareBitcoinApiService apiService, ILogger logger, TimeProvider? timeProvider = null)
    {
        _apiService = apiService;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<LightningNodeBalance> GetBalance(string accountId, CancellationToken cancellation = default)
    {
        await _balanceLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(accountId, out var entry) &&
                _clock.GetElapsedTime(entry.timestamp) < CacheLifetime)
                return entry.balance;

            var response = await _apiService.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts",
                useSimpleAuth: true, cancellation: cancellation).ConfigureAwait(false);
            var accounts = JObject.Parse(response)["accounts"] as JArray
                ?? throw new InvalidOperationException("Bare Bitcoin returned no account list");
            var account = accounts.FirstOrDefault(a => a["id"]?.Value<string>() == accountId)
                ?? throw new InvalidOperationException("The configured Bitcoin account is not available to this API key");
            var available = account["availableBtc"]?.Value<decimal>()
                ?? throw new InvalidOperationException("Bare Bitcoin returned no available balance for the account");
            var balance = new LightningNodeBalance
            {
                OffchainBalance = new OffchainBalance { Local = LightMoney.Satoshis(checked((long)(available * 100_000_000))) }
            };
            _cache[accountId] = (balance, _clock.GetTimestamp());
            return balance;
        }
        finally { _balanceLock.Release(); }
    }
}
