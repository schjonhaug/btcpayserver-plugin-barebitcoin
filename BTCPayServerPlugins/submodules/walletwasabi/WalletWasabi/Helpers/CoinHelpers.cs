using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Wallets;

namespace WalletWasabi.Helpers;

public static class CoinHelpers
{
	public static bool IsPrivate<TCoin>(this TCoin coin, IWallet? wallet)
		where TCoin : class, ISmartCoin, IEquatable<TCoin>
	{
		return (!wallet.ConsiderEntryProximity || coin.IsSufficientlyDistancedFromExternalKeys) && coin.AnonymitySet >= wallet.AnonScoreTarget;
	}

	public static bool IsSemiPrivate<TCoin>(this TCoin coin, IWallet? wallet, int semiPrivateThreshold = Constants.SemiPrivateThreshold)
		where TCoin : class, ISmartCoin, IEquatable<TCoin>
	{
		return !IsRedCoin(coin, semiPrivateThreshold) && !IsPrivate(coin, wallet);
	}

	public static bool IsRedCoin<TCoin>(this TCoin coin, int semiPrivateThreshold = Constants.SemiPrivateThreshold)
		where TCoin : class, ISmartCoin, IEquatable<TCoin>
	{
		return coin.AnonymitySet < semiPrivateThreshold;
	}
}
