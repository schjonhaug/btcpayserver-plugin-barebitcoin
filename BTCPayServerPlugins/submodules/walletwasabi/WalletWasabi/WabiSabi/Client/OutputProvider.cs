using NBitcoin;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.Wallets;
using WabiSabi.Crypto.Randomness;
using WalletWasabi.Helpers;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client.Decomposer;

namespace WalletWasabi.WabiSabi.Client;

public class OutputProvider
{
	private readonly IWallet? _wallet;

	private WasabiRandom Random { get; }

	public OutputProvider(IWallet? wallet,  WasabiRandom? random = null)
	{
		_wallet = wallet;

		Random = random ?? SecureRandom.Instance;
	}


	public virtual async Task<(IEnumerable<TxOut>, Dictionary<TxOut, PendingPayment> batchedPayments)> GetOutputs(
		uint256 roundId,
		RoundParameters roundParameters,
		ImmutableArray<AliceClient> registeredCoins,
		IEnumerable<Money> theirCoinEffectiveValues,
		int availableVsize)
	{
		var scrriptTypes = await _wallet.DestinationProvider.GetScriptTypeAsync().ConfigureAwait(false);
		AmountDecomposer amountDecomposer = new(
			roundParameters.MiningFeeRate,
			roundParameters.CalculateMinReasonableOutputAmount(scrriptTypes),
			roundParameters.AllowedOutputAmounts.Max,
			availableVsize,
			scrriptTypes,
			Random,
			_wallet.MinimumDenominationAmount,
			_wallet.AllowedDenominations?.Any() is true? _wallet.AllowedDenominations : null);

		var registeredCoinEffectiveValues = registeredCoins.Select(client => client.EffectiveValue);


		var remainingPendingPayments = _wallet.BatchPayments
			? (await _wallet.DestinationProvider.GetPendingPaymentsAsync(roundParameters).ConfigureAwait(false))
			.Where(payment =>
				roundParameters.AllowedOutputTypes.Contains(
					payment.Destination.ScriptPubKey.GetScriptType()))
			.Where(payment => roundParameters.AllowedOutputAmounts.Contains(payment.Value))
			.ToList()
			: new List<PendingPayment>();

		IEnumerable<Output> outputValues;
		var paymentsToBatch = new List<PendingPayment>();
		if (remainingPendingPayments.Any())
		{
			var effectiveValueSum = registeredCoinEffectiveValues.Sum().ToDecimal(MoneyUnit.BTC);
			var pendingPaymentBatchSum = 0m;

			// Loop through the pending payments and handle each payment by subtracting the payment amount from the total value of the selected coins
			var potentialPayments = remainingPendingPayments
				.Where(payment =>
					payment.ToTxOut().EffectiveCost(roundParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <=
					(effectiveValueSum - pendingPaymentBatchSum)).ToList();

			while (potentialPayments.Any())
			{
				var payment = potentialPayments.RandomElement(Random);
				var txout = payment.ToTxOut();
				// we have to check that we fit at least one change output at the end if we batch this payment
				if (availableVsize < txout.ScriptPubKey.EstimateOutputVsize() +
				    amountDecomposer.ChangeScriptType.EstimateOutputVsize())
				{
					potentialPayments.Remove(payment);
					continue;
				}

				var cost = txout.EffectiveCost(roundParameters.MiningFeeRate)
					.ToDecimal(MoneyUnit.BTC);
				if (!await payment.PaymentStarted.Invoke().ConfigureAwait(false))
				{
					potentialPayments.Remove(payment);
					continue;
				}

				paymentsToBatch.Add(payment);
				pendingPaymentBatchSum += cost;
				potentialPayments.Remove(payment);
				potentialPayments = potentialPayments
					.Where(payment =>
						payment.ToTxOut().EffectiveCost(roundParameters.MiningFeeRate)
							.ToDecimal(MoneyUnit.BTC) <= (effectiveValueSum - pendingPaymentBatchSum)).ToList();

			}

			var remainder = effectiveValueSum - pendingPaymentBatchSum;
			outputValues =
				amountDecomposer.Decompose(new[] {new Money(remainder, MoneyUnit.BTC)}, theirCoinEffectiveValues);
		}
		else
		{
			outputValues = amountDecomposer.Decompose(registeredCoinEffectiveValues, theirCoinEffectiveValues);
		}

		var privateEnough = registeredCoins.All(client => client.SmartCoin.AnonymitySet >= _wallet.AnonScoreTarget );
		Dictionary<TxOut, PendingPayment> batchedPayments =
			paymentsToBatch.ToDictionary(payment => new TxOut(payment.Value, payment.Destination.ScriptPubKey));

		var decomposedOut = await GetTxOuts(outputValues, _wallet.DestinationProvider, privateEnough).ConfigureAwait(false);

		return (decomposedOut.Concat(batchedPayments.Keys), batchedPayments);
	}



	internal static async Task<IEnumerable<TxOut>> GetTxOuts(IEnumerable<Output> outputValues,
		IDestinationProvider destinationProvider, bool privateEnough)
	{

		var nonMixedOutputs = outputValues.Where(output => !BlockchainAnalyzer.StdDenoms.Contains(output.Amount));
		var mixedOutputs = outputValues.Where(output => BlockchainAnalyzer.StdDenoms.Contains(output.Amount));


		// Get as many destinations as outputs we need.
		var destinations = (await destinationProvider
			.GetNextDestinationsAsync(mixedOutputs.Count(), true, privateEnough).ConfigureAwait(false)).Zip(mixedOutputs,
			(destination, output) => new TxOut(output.Amount, destination));
		var destinationsNonMixed =
			(await destinationProvider.GetNextDestinationsAsync(nonMixedOutputs.Count(), false, privateEnough).ConfigureAwait(false))
			.Zip(nonMixedOutputs, (destination, output) => new TxOut(output.Amount, destination));

		var outputTxOuts = destinations.Concat(destinationsNonMixed);
		return outputTxOuts;
	}
}
