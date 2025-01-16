#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class BareBitcoinBalanceService
{
    private readonly BareBitcoinApiService _apiService;
    private readonly ILogger _logger;

    // Balance caching
    private static readonly SemaphoreSlim _balanceLock = new SemaphoreSlim(1, 1);
    private LightningNodeBalance? _cachedBalance;
    private DateTime _lastBalanceCheck = DateTime.MinValue;
    private static readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(5);

    public BareBitcoinBalanceService(BareBitcoinApiService apiService, ILogger logger)
    {
        _apiService = apiService;
        _logger = logger;
    }

    public async Task<LightningNodeBalance> GetBalance(string accountId, CancellationToken cancellation = default)
    {
        try 
        {
            await _balanceLock.WaitAsync(cancellation);
            try
            {
                var now = DateTime.UtcNow;
                if (_cachedBalance != null && (now - _lastBalanceCheck) < _cacheTimeout)
                {
                    _logger.LogInformation("Using cached balance from {LastCheck}", _lastBalanceCheck);
                    return _cachedBalance;
                }

                _logger.LogInformation("Getting balance from BareBitcoin (cache expired or not set)");
                var response = await _apiService.MakeAuthenticatedRequest("GET", "/v1/user/bitcoin-accounts");
                _logger.LogInformation("Received balance response: {response}", response);
                
                // Parse response according to OpenAPI spec
                var accounts = JObject.Parse(response)["accounts"] as JArray;
                if (accounts == null || !accounts.Any())
                {
                    _logger.LogWarning("No bitcoin accounts found in response");
                    return new LightningNodeBalance();
                }

                // If we have an accountId, find that specific account
                var account = accountId != null 
                    ? accounts.FirstOrDefault(a => a["id"]?.ToString() == accountId)
                    : accounts.First();

                if (account == null)
                {
                    _logger.LogWarning("Account {AccountId} not found in response", accountId);
                    return new LightningNodeBalance();
                }

                // Get availableBtc and convert to satoshis (1 BTC = 100,000,000 sats)
                var availableBtc = account["availableBtc"]?.Value<double>() ?? 0;
                var satoshis = (long)(availableBtc * 100_000_000);

                _logger.LogInformation("Creating LightningNodeBalance response with {AvailableBtc} BTC ({Satoshis} sats)", availableBtc, satoshis);
                
                _cachedBalance = new LightningNodeBalance()
                {
                    OffchainBalance = new OffchainBalance()
                    {
                        Local = LightMoney.Satoshis(satoshis)
                    }
                };
                _lastBalanceCheck = now;
                
                return _cachedBalance;
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
} 