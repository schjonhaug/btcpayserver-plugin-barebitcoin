using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WabiSabi.Crypto.Randomness;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.Wallets;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.WabiSabi.Client;

public class CoinJoinTrackerFactory
{
	private readonly string? _coordinatorName;

	public CoinJoinTrackerFactory(IWasabiHttpClientFactory httpClientFactory,
		RoundStateUpdater roundStatusUpdater,
		CoinJoinConfiguration coinJoinConfiguration,
		CancellationToken cancellationToken,
		string? coordinatorName)
	{
		HttpClientFactory = httpClientFactory;
		_coordinatorName = coordinatorName;
		RoundStatusUpdater = roundStatusUpdater;
		CoinJoinConfiguration = coinJoinConfiguration;
		CancellationToken = cancellationToken;
		LiquidityClueProvider = new LiquidityClueProvider();
	}

	private IWasabiHttpClientFactory HttpClientFactory { get; }
	private RoundStateUpdater RoundStatusUpdater { get; }
	private CoinJoinConfiguration CoinJoinConfiguration { get; }
	private CancellationToken CancellationToken { get; }
	private LiquidityClueProvider LiquidityClueProvider { get; }

	public async Task<CoinJoinTracker> CreateAndStartAsync(IWallet wallet, Func<Task<(IEnumerable<SmartCoin> Candidates, IEnumerable<SmartCoin> Ineligible)>> coinCandidatesFunc, bool stopWhenAllMixed, bool overridePlebStop)
	{
		await LiquidityClueProvider.InitLiquidityClueAsync(wallet).ConfigureAwait(false);

		if (wallet.KeyChain is null)
		{
			throw new NotSupportedException("Wallet has no key chain.");
		}

		var coinSelector = CoinJoinCoinSelector.FromWallet(wallet);
		var outputProvider = new OutputProvider(wallet, InsecureRandom.Instance);
		var coinJoinClient = new CoinJoinClient(
			HttpClientFactory,
			wallet,
			wallet.KeyChain,
			outputProvider,
			RoundStatusUpdater,
			coinSelector,
			CoinJoinConfiguration,
			LiquidityClueProvider,

			wallet.FeeRateMedianTimeFrame,
			TimeSpan.FromMinutes(1),
			wallet.GetCoinSelector(),
			_coordinatorName);

		return new CoinJoinTracker(wallet, coinJoinClient, coinCandidatesFunc, stopWhenAllMixed, overridePlebStop, wallet, CancellationToken);
	}
}
