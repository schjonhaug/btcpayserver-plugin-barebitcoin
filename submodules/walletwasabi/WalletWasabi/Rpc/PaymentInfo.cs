using NBitcoin;

namespace WalletWasabi.Rpc;

public class PaymentInfo
{
	public BitcoinAddress Sendto { get; init; }
	public Money Amount { get; init; }
	public string Label { get; init; }
	public bool SubtractFee { get; init; }
}
