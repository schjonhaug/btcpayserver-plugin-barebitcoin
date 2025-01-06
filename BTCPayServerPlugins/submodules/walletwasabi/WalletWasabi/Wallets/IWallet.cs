using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using LogLevel = WalletWasabi.Logging.LogLevel;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using PendingPayment = WalletWasabi.WabiSabi.Client.PendingPayment;

namespace WalletWasabi.Wallets;

public enum ConsolidationModeType
{
	Always,
	Never,
	WhenLowFee,
	WhenLowFeeAndManyUTXO
}

public interface IWallet
{
	void Log(LogLevel logLevel, string logMessage, string? coordinator, string callerFilePath = "",
		string callerMemberName = "",
		int callerLineNumber = -1);
	string WalletName { get; }
	WalletId WalletId { get; }
	bool IsUnderPlebStop { get; }
	bool IsMixable(string? coordinator);

	/// <summary>
	/// Watch only wallets have no key chains.
	/// </summary>
	IKeyChain? KeyChain { get; }

	IDestinationProvider DestinationProvider { get; }
	OutputProvider GetOutputProvider (string coordinatorName);

	int AnonScoreTarget { get; }
	ConsolidationModeType ConsolidationMode { get; }
	TimeSpan FeeRateMedianTimeFrame { get; }
	int ExplicitHighestFeeTarget { get; }
	int LowFeeTarget { get; }
bool ConsiderEntryProximity { get; }
	bool RedCoinIsolation { get; }
	bool BatchPayments { get; }
	long? MinimumDenominationAmount { get; }
	long[]? AllowedDenominations { get; }

	public enum MixingReason
	{
		PreliminaryMixConclusion,
		NotPrivate,
		Payment,
		ExtraJoin,
		WalletForward,
		Consolidation,
	}

	Task<MixingReason[]> ShouldMix(string? coordinatorName, bool? isLowFee = null, bool? anyPayments = null);

	Task<IEnumerable<SmartCoin>> GetCoinjoinCoinCandidatesAsync(string? coordinatorname);

	Task<IEnumerable<SmartTransaction>> GetTransactionsAsync();

	IRoundCoinSelector? GetCoinSelector()
	{
		return null;
	}

	bool IsRoundOk(RoundParameters coinjoinStateParameters, string? coordinatorName);
	Task CompletedCoinjoin(CoinJoinTracker finishedCoinJoin);
}

public interface IRoundCoinSelector
{
	Task<(ImmutableList<SmartCoin> selected, Func<IEnumerable<AliceClient>, Task<bool>> acceptableRegistered, Func<ImmutableArray<AliceClient>, (IEnumerable<TxOut> outputTxOuts, Dictionary<TxOut, PendingPayment> batchedPayments), TransactionWithPrecomputedData, RoundState, Task<bool>> acceptableOutputs)>
		SelectCoinsAsync((IEnumerable<SmartCoin> Candidates, IEnumerable<SmartCoin> Ineligible) candidates,
			RoundParameters roundParameters, Money liquidityClue, SecureRandom secureRandom, string? coordinatorName);
}
