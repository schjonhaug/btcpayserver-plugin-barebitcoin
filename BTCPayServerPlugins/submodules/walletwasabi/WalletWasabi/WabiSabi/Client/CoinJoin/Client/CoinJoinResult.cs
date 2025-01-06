using System.Collections.Generic;
using NBitcoin;
using System.Collections.Immutable;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace WalletWasabi.WabiSabi.Client;

public abstract record CoinJoinResult;

public record SuccessfulCoinJoinResult(
	ImmutableList<SmartCoin> Coins,
	ImmutableList<TxOut> Outputs,
	ImmutableDictionary<TxOut, PendingPayment> HandledPayments,
	Transaction UnsignedCoinJoin, uint256 RoundId) : CoinJoinResult;

public record FailedCoinJoinResult : CoinJoinResult;

public record DisruptedCoinJoinResult(ImmutableList<SmartCoin> SignedCoins, bool abandonAndAllSubsequentBlames) : CoinJoinResult; 
