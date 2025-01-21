using Newtonsoft.Json;

namespace WalletWasabi.Backend.Models.Responses;

public class VersionsResponse
{
	public string ClientVersion { get; init; }

	// KEEP THE TYPO IN IT! Otherwise the response would not be backwards compatible.
	[JsonProperty(PropertyName = "BackenMajordVersion")]
	public string BackendMajorVersion { get; init; }

	[JsonProperty(PropertyName = "LegalDocumentsVersion")]
	public string Ww1LegalDocumentsVersion { get; init; }

	public string Ww2LegalDocumentsVersion { get; init; }

	public string CommitHash { get; init; }
}
