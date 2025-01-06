using NBitcoin;
using System.Collections.Generic;
using System.Threading.Tasks;
using WalletWasabi.WabiSabi.Backend.Rounds;

namespace WalletWasabi.WabiSabi.Client;

public interface IDestinationProvider
{
	Task<IEnumerable<IDestination>> GetNextDestinationsAsync(int count, bool mixedOutputs, bool privateEnough);

	Task<IEnumerable<PendingPayment>> GetPendingPaymentsAsync(RoundParameters roundParameters);

	Task<ScriptType[]> GetScriptTypeAsync();
}
