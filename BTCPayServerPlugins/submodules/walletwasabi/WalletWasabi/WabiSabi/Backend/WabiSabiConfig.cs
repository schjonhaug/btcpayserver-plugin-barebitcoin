using NBitcoin;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using WalletWasabi.Bases;
using WalletWasabi.JsonConverters;
using WalletWasabi.JsonConverters.Bitcoin;
using WalletWasabi.JsonConverters.Timing;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Backend.DoSPrevention;
using WalletWasabi.Helpers;

namespace WalletWasabi.WabiSabi.Backend;

[JsonObject(MemberSerialization.OptIn)]
public class WabiSabiConfig : ConfigBase
{
	public WabiSabiConfig() : base()
	{
	}

	public WabiSabiConfig(string filePath) : base(filePath)
	{
	}

	[DefaultValue(108)]
	[JsonProperty(PropertyName = "ConfirmationTarget", DefaultValueHandling = DefaultValueHandling.Populate)]
	public uint ConfirmationTarget { get; set; } = 108;

	[DefaultValueMoneyBtc("0.1")]
	[JsonProperty(PropertyName = "DoSSeverity", DefaultValueHandling = DefaultValueHandling.Populate)]
	[JsonConverter(typeof(MoneyBtcJsonConverter))]
	public Money DoSSeverity { get; set; } = Money.Coins(0.1m);

	[DefaultValueTimeSpan("31d 0h 0m 0s")]
	[JsonProperty(PropertyName = "DoSMinTimeForFailedToVerify", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan DoSMinTimeForFailedToVerify { get; set; } = TimeSpan.FromDays(31);

	[DefaultValueTimeSpan("1d 0h 0m 0s")]
	[JsonProperty(PropertyName = "DoSMinTimeForCheating", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan DoSMinTimeForCheating { get; set; } = TimeSpan.FromDays(1);

	[DefaultValue(0.2)]
	[JsonProperty(PropertyName = "DoSPenaltyFactorForDisruptingConfirmation", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double DoSPenaltyFactorForDisruptingConfirmation { get; set; } = 0.2;

	[DefaultValue(1.0)]
	[JsonProperty(PropertyName = "DoSPenaltyFactorForDisruptingSignalReadyToSign", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double DoSPenaltyFactorForDisruptingSignalReadyToSign { get; set; } = 1.0;

	[DefaultValue(1.0)]
	[JsonProperty(PropertyName = "DoSPenaltyFactorForDisruptingSigning", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double DoSPenaltyFactorForDisruptingSigning { get; set; } = 1.0;

	[DefaultValue(3.0)]
	[JsonProperty(PropertyName = "DoSPenaltyFactorForDisruptingByDoubleSpending", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double DoSPenaltyFactorForDisruptingByDoubleSpending { get; set; } = 3.0;

	[DefaultValueTimeSpan("0d 0h 20m 0s")]
	[JsonProperty(PropertyName = "DoSMinTimeInPrison", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan DoSMinTimeInPrison { get; set; } = TimeSpan.FromMinutes(20);

	[DefaultValueMoneyBtc("0.00005")]
	[JsonProperty(PropertyName = "MinRegistrableAmount", DefaultValueHandling = DefaultValueHandling.Populate)]
	[JsonConverter(typeof(MoneyBtcJsonConverter))]
	public Money MinRegistrableAmount { get; set; } = Money.Coins(0.00005m);

	/// <summary>
	/// The width of the range proofs are calculated from this, so don't choose stupid numbers.
	/// </summary>
	[DefaultValueMoneyBtc("43000")]
	[JsonProperty(PropertyName = "MaxRegistrableAmount", DefaultValueHandling = DefaultValueHandling.Populate)]
	[JsonConverter(typeof(MoneyBtcJsonConverter))]
	public Money MaxRegistrableAmount { get; set; } = Money.Coins(43_000m);

	[DefaultValue(true)]
	[JsonProperty(PropertyName = "AllowNotedInputRegistration", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool AllowNotedInputRegistration { get; set; } = true;

	[DefaultValueTimeSpan("0d 1h 0m 0s")]
	[JsonProperty(PropertyName = "StandardInputRegistrationTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan StandardInputRegistrationTimeout { get; set; } = TimeSpan.FromHours(1);

	[DefaultValueTimeSpan("0d 0h 3m 0s")]
	[JsonProperty(PropertyName = "BlameInputRegistrationTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan BlameInputRegistrationTimeout { get; set; } = TimeSpan.FromMinutes(3);

	[DefaultValueTimeSpan("0d 0h 1m 0s")]
	[JsonProperty(PropertyName = "ConnectionConfirmationTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan ConnectionConfirmationTimeout { get; set; } = TimeSpan.FromMinutes(1);

	[DefaultValueTimeSpan("0d 0h 1m 0s")]
	[JsonProperty(PropertyName = "OutputRegistrationTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan OutputRegistrationTimeout { get; set; } = TimeSpan.FromMinutes(1);

	[DefaultValueTimeSpan("0d 0h 1m 0s")]
	[JsonProperty(PropertyName = "TransactionSigningTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan TransactionSigningTimeout { get; set; } = TimeSpan.FromMinutes(1);

	[DefaultValueTimeSpan("0d 0h 3m 0s")]
	[JsonProperty(PropertyName = "FailFastOutputRegistrationTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan FailFastOutputRegistrationTimeout { get; set; } = TimeSpan.FromMinutes(3);

	[DefaultValueTimeSpan("0d 0h 1m 0s")]
	[JsonProperty(PropertyName = "FailFastTransactionSigningTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan FailFastTransactionSigningTimeout { get; set; } = TimeSpan.FromMinutes(1);

	[DefaultValueTimeSpan("0d 0h 5m 0s")]
	[JsonProperty(PropertyName = "RoundExpiryTimeout", DefaultValueHandling = DefaultValueHandling.Populate)]
	public TimeSpan RoundExpiryTimeout { get; set; } = TimeSpan.FromMinutes(5);

	[DefaultValue(100)]
	[JsonProperty(PropertyName = "MaxInputCountByRound", DefaultValueHandling = DefaultValueHandling.Populate)]
	public int MaxInputCountByRound { get; set; } = 100;

	[DefaultValue(0.5)]
	[JsonProperty(PropertyName = "MinInputCountByRoundMultiplier", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double MinInputCountByRoundMultiplier { get; set; } = 0.5;

	public int MinInputCountByRound => Math.Max(1, (int)(MaxInputCountByRound * MinInputCountByRoundMultiplier));

	[DefaultValue(0.4)]
	[JsonProperty(PropertyName = "MinInputCountByBlameRoundMultiplier", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double MinInputCountByBlameRoundMultiplier { get; set; } = 0.4;

	public int MinInputCountByBlameRound => Math.Max(1, (int)(MaxInputCountByRound * MinInputCountByBlameRoundMultiplier));

	[DefaultValue(375)]
	[JsonProperty(PropertyName = "RoundDestroyerThreshold", DefaultValueHandling = DefaultValueHandling.Populate)]
	public int RoundDestroyerThreshold { get; set; } = 375;



	public class CoordinatorSplit
	{
		public decimal Ratio { get; set; }
		public string Type  { get; set; }
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
		public string Value { get; set; }
	}
	[JsonProperty(PropertyName = "CoordinatorSplits", DefaultValueHandling = DefaultValueHandling.Populate)]
	public List<CoordinatorSplit> CoordinatorSplits { get; set; } = new();

	[DefaultValueMoneyBtc("0.1")]
	[JsonProperty(PropertyName = "MaxSuggestedAmountBase", DefaultValueHandling = DefaultValueHandling.Populate)]
	[JsonConverter(typeof(MoneyBtcJsonConverter))]
	public Money MaxSuggestedAmountBase { get; set; } = Money.Coins(0.1m);

	[DefaultValue(1)]
	[JsonProperty(PropertyName = "RoundParallelization", DefaultValueHandling = DefaultValueHandling.Populate)]
	public int RoundParallelization { get; set; } = 1;

	[DefaultValue(false)]
	[JsonProperty(PropertyName = "WW200CompatibleLoadBalancing", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool WW200CompatibleLoadBalancing { get; set; } = false;

	[DefaultValue(0.75)]
	[JsonProperty(PropertyName = "WW200CompatibleLoadBalancingInputSplit", DefaultValueHandling = DefaultValueHandling.Populate)]
	public double WW200CompatibleLoadBalancingInputSplit { get; set; } = 0.75;

	[DefaultValue("CoinJoinCoordinatorIdentifier")]
	[JsonProperty(PropertyName = "CoordinatorIdentifier", DefaultValueHandling = DefaultValueHandling.Populate)]
	public string CoordinatorIdentifier { get; set; } = "CoinJoinCoordinatorIdentifier";

	[DefaultValue(true)]
	[JsonProperty(PropertyName = "AllowP2wpkhInputs", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool AllowP2wpkhInputs { get; set; } = true;

	[DefaultValue(true)]
	[JsonProperty(PropertyName = "AllowP2trInputs", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool AllowP2trInputs { get; set; } = true;


	[DefaultValue(false)]
	[JsonProperty(PropertyName = "DelayTransactionSigning", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool DelayTransactionSigning { get; set; } = false;

	[DefaultValue(true)]
	[JsonProperty(PropertyName = "IsCoordinationEnabled", DefaultValueHandling = DefaultValueHandling.Populate)]
	public bool IsCoordinationEnabled { get; set; } = true;
	public ImmutableSortedSet<ScriptType> AllowedInputTypes => GetScriptTypes(AllowP2wpkhInputs, AllowP2trInputs, false, false, false);

	[JsonProperty(PropertyName = "AllowedOutputTypes", ItemConverterType = typeof(StringEnumConverter))]
	public ImmutableSortedSet<ScriptType> AllowedOutputTypes { get; set; } = ImmutableSortedSet.Create(ScriptType.P2WPKH, ScriptType.Taproot);

	public async Task<(CoordinatorSplit split, Script? script)[]> GetNextCleanCoordinatorScripts(CoordinatorScriptResolver coordinatorScriptResolver , IHttpClientFactory httpClient, Round round, CancellationToken cancellationToken)
	{
		var totalRatio = CoordinatorSplits.Sum(split => split.Ratio);

		var splitsTasks = CoordinatorSplits.Select(async split =>
		{
			try
			{

				return (split, await coordinatorScriptResolver.ResolveScript(split.Type, split.Value, round.Parameters.Network, cancellationToken));

			}
			catch (Exception e)
			{
				return (split, null);
			}
		});
		return await Task.WhenAll(splitsTasks).ConfigureAwait(false);

	}



	public abstract class CoordinatorScriptResolver
	{
		public abstract Task<Script?> ResolveScript(string type, string value, Network network, CancellationToken cancellationToken);
	}

	public DoSConfiguration GetDoSConfiguration() =>
		new(
			SeverityInBitcoinsPerHour: DoSSeverity.ToDecimal(MoneyUnit.BTC),
			MinTimeForFailedToVerify: DoSMinTimeForFailedToVerify,
			MinTimeForCheating: DoSMinTimeForCheating,
			PenaltyFactorForDisruptingConfirmation: (decimal) DoSPenaltyFactorForDisruptingConfirmation,
			PenaltyFactorForDisruptingSignalReadyToSign: (decimal) DoSPenaltyFactorForDisruptingSignalReadyToSign,
			PenaltyFactorForDisruptingSigning: (decimal) DoSPenaltyFactorForDisruptingSigning,
			PenaltyFactorForDisruptingByDoubleSpending: (decimal) DoSPenaltyFactorForDisruptingByDoubleSpending,
			MinTimeInPrison: DoSMinTimeInPrison);

	private static ImmutableSortedSet<ScriptType> GetScriptTypes(bool p2wpkh, bool p2tr, bool p2pkh, bool p2sh, bool p2wsh)
	{
		var scriptTypes = new List<ScriptType>();
		if (p2wpkh)
		{
			scriptTypes.Add(ScriptType.P2WPKH);
		}
		if (p2tr)
		{
			scriptTypes.Add(ScriptType.Taproot);
		}
		if (p2pkh)
		{
			scriptTypes.Add(ScriptType.P2PKH);
		}
		if (p2sh)
		{
			scriptTypes.Add(ScriptType.P2SH);
		}
		if (p2wsh)
		{
			scriptTypes.Add(ScriptType.P2WSH);
		}

		// When adding new script types, please see
		// https://github.com/WalletWasabi/WalletWasabi/issues/5440

		return scriptTypes.ToImmutableSortedSet();
	}
}
