#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Service responsible for retrieving and caching BareBitcoin account balances.
/// Implements a simple caching mechanism to reduce API calls while ensuring balance data stays relatively fresh.
/// </summary>
public class BareBitcoinBalanceService
{
    private readonly BareBitcoinApiService _apiService;
    private readonly ILogger _logger;

    // Balance caching mechanism
    // Uses a semaphore to ensure thread-safe access to cached data
    private static readonly SemaphoreSlim _balanceLock = new SemaphoreSlim(1, 1);
    private LightningNodeBalance? _cachedBalance;
    private DateTime _lastBalanceCheck = DateTime.MinValue;
    // Cache timeout of 5 seconds to prevent too frequent API calls while keeping data fresh
    private static readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(5);

    public BareBitcoinBalanceService(BareBitcoinApiService apiService, ILogger logger)
    {
        _apiService = apiService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the current balance for a specific account.
    /// Implements caching to reduce API calls - returns cached balance if available and not expired.
    /// </summary>
    /// <param name="accountId">The ID of the account to get the balance for</param>
    /// <param name="cancellation">Cancellation token for async operations</param>
    /// <returns>A LightningNodeBalance containing the current balance information</returns>
    public async Task<LightningNodeBalance> GetBalance(string accountId, CancellationToken cancellation = default)
    {
        try 
        {
            // Ensure thread-safe access to cached data
            await _balanceLock.WaitAsync(cancellation);
            try
            {
                // Check if we have a valid cached balance
                if (_lastBalanceCheck != DateTime.MinValue && 
                    DateTime.UtcNow - _lastBalanceCheck < TimeSpan.FromMinutes(1) &&
                    _cachedBalance != null)
                {
                    _logger.LogDebug("Using cached balance from {LastCheck}", _lastBalanceCheck);
                    return _cachedBalance;
                }

                // Cache expired or not set - fetch fresh balance from API
                _logger.LogDebug("Getting balance from BareBitcoin (cache expired or not set)");
                var response = await _apiService.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts", useSimpleAuth: true);
                _logger.LogDebug("Received balance response: {response}", response);
                
                // Parse response according to OpenAPI spec
                var accounts = JObject.Parse(response)["accounts"] as JArray;
                
                // Handle case where no accounts are found
                if (accounts == null || !accounts.Any())
                {
                    _logger.LogWarning("No bitcoin accounts found in response");
                    return CreateZeroBalance();
                }

                // Find the specific account we're interested in
                var account = accounts.FirstOrDefault(a => a["id"]?.ToString() == accountId);
                if (account == null)
                {
                    _logger.LogWarning("Account {AccountId} not found in response", accountId);
                    return CreateZeroBalance();
                }

                // Convert balance from BTC to satoshis
                var availableBtc = account["availableBtc"]?.Value<decimal>() ?? 0m;
                var satoshis = (long)(availableBtc * 100_000_000);
                _logger.LogDebug("Creating LightningNodeBalance response with {AvailableBtc} BTC ({Satoshis} sats)", availableBtc, satoshis);
                
                // Create and cache the balance response
                var balance = new LightningNodeBalance
                {
                    OffchainBalance = new OffchainBalance
                    {
                        Local = LightMoney.Satoshis(satoshis)
                    }
                };

                _cachedBalance = balance;
                _lastBalanceCheck = DateTime.UtcNow;
                
                return balance;
            }
            finally
            {
                _balanceLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting balance from BareBitcoin");
            throw;
        }
    }

    /// <summary>
    /// Creates a zero balance response for cases where no balance data is available
    /// </summary>
    private static LightningNodeBalance CreateZeroBalance() => new()
    {
        OffchainBalance = new OffchainBalance
        {
            Local = LightMoney.Zero
        }
    };
} 