using WalletWasabi.Tor.Http;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.WabiSabi.Backend.PostRequests;

namespace WalletWasabi.WebClients.Wasabi;

public interface IWasabiHttpClientFactory
{

	(PersonCircuit, IWabiSabiApiRequestHandler) NewHttpClientWithPersonCircuit()
	{
		PersonCircuit personCircuit = new();
		var httpClient = NewWabiSabiApiRequestHandler(Mode.SingleCircuitPerLifetime, personCircuit);
		return (personCircuit, httpClient);
	}

	IWabiSabiApiRequestHandler NewHttpClientWithDefaultCircuit()
	{
		return NewWabiSabiApiRequestHandler(Mode.DefaultCircuit);
	}

	IWabiSabiApiRequestHandler NewHttpClientWithCircuitPerRequest()
	{
		return NewWabiSabiApiRequestHandler(Mode.NewCircuitPerRequest);
	}

	/// <remarks>This is a low-level method. Unless necessary, use a preceding convenience method.</remarks>
	IWabiSabiApiRequestHandler NewWabiSabiApiRequestHandler(Mode mode, ICircuit? circuit = null);
}
