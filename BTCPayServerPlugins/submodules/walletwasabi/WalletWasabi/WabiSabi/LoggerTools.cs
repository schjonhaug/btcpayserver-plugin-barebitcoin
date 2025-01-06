using NBitcoin;
using System.Runtime.CompilerServices;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi;

public static class LoggerTools
{
	public static void Log(this Round round, IWallet? wallet, string? coordinator, LogLevel logLevel, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		var roundState = RoundState.FromRound(round);
		roundState.Log(wallet, coordinator,logLevel, logMessage, callerFilePath, callerMemberName, callerLineNumber);
	}

	public static void LogInfo(this Round round,IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(round,wallet, coordinator,LogLevel.Info, logMessage, callerFilePath, callerMemberName: callerMemberName, callerLineNumber);
	}

	public static void LogWarning(this Round round,IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(round,wallet,  coordinator,LogLevel.Warning, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber);
	}

	public static void LogError(this Round round,IWallet? wallet,string? coordinator,  string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(round, wallet,coordinator, LogLevel.Error, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void Log(this RoundState roundState, IWallet? wallet, string? coordinator,  LogLevel logLevel, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		string round = !roundState.IsBlame ? "Round" : "Blame Round";
		round += $" ({roundState.Id.ToString()[..7] + "..." + roundState.Id.ToString()[^7..]})";
		if (wallet is null)
		{

			Logger.Log(logLevel, $"{round}: {logMessage}", callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
		}
		else
		{
			wallet.Log(logLevel,$"{round}: {logMessage}", coordinator:coordinator, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
		}
	}

	public static void LogInfo(this RoundState roundState, IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(roundState, wallet,coordinator, LogLevel.Info, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogDebug(this RoundState roundState, IWallet? wallet,string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(roundState,wallet, coordinator, LogLevel.Debug,logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void Log(this IWallet? wallet,string? coordinator, LogLevel logLevel, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{

		wallet.Log(logLevel, logMessage, coordinator:coordinator, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogInfo(this IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(wallet, coordinator, LogLevel.Info, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogDebug(this IWallet? wallet,string? coordinator,  string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(wallet, coordinator, LogLevel.Debug, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogWarning(this IWallet? wallet,string? coordinator,  string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(wallet, coordinator, LogLevel.Warning, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogError(this IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(wallet, coordinator,LogLevel.Error, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}

	public static void LogTrace(this IWallet? wallet, string? coordinator, string logMessage, [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = -1)
	{
		Log(wallet, coordinator,LogLevel.Trace, logMessage, callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);
	}
}
