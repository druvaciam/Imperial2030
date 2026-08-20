using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers;

/// <summary>
/// Builds the <see cref="LocalizedToast"/> payloads the server pushes over SignalR, so the same
/// message shape is produced whether the action came from a human (GamesController) or a bot
/// (BotService) rather than being composed twice.
/// </summary>
public static class ToastBuilder
{
    /// <summary>
    /// The toast for a completed bond purchase or upgrade, including whether it flipped control of
    /// the nation. Each combination maps to its own resource key so translators get a whole
    /// sentence instead of a base message with a glued-on suffix.
    /// </summary>
    public static LocalizedToast BuildInvestmentToast(
        string playerName, Nation nation, int cost, int? tradeInCost, bool tookControl, string? previousControllerName)
    {
        var args = new List<ToastArg> { ToastArg.Text(playerName), ToastArg.Of(nation) };

        if (tradeInCost.HasValue)
        {
            args.Add(ToastArg.Text(tradeInCost.Value.ToString()));
        }
        args.Add(ToastArg.Text(cost.ToString()));

        string code;
        if (!tookControl)
        {
            code = tradeInCost.HasValue ? ToastCodes.InvestmentUpgraded : ToastCodes.InvestmentBought;
        }
        else if (previousControllerName != null)
        {
            args.Add(ToastArg.Text(previousControllerName));
            code = tradeInCost.HasValue
                ? ToastCodes.InvestmentUpgradedTookControlFrom
                : ToastCodes.InvestmentBoughtTookControlFrom;
        }
        else
        {
            code = tradeInCost.HasValue
                ? ToastCodes.InvestmentUpgradedTookControl
                : ToastCodes.InvestmentBoughtTookControl;
        }

        return new LocalizedToast { Code = code, Args = args };
    }

    /// <summary>A Swiss Bank holder's decision on whether to force a nation to stop on Investor.</summary>
    public static LocalizedToast BuildSwissBankToast(string responderName, Nation nation, bool isForceStop) =>
        new()
        {
            Code = isForceStop ? ToastCodes.SwissForcedStop : ToastCodes.SwissPassed,
            Args = { ToastArg.Text(responderName), ToastArg.Of(nation) }
        };

    /// <summary>A defender's response to a pending battle.</summary>
    public static LocalizedToast BuildBattleResponseToast(Nation respondingNation, Nation aggressorNation, bool isFight) =>
        new()
        {
            Code = isFight ? ToastCodes.BattleFight : ToastCodes.BattlePeace,
            Args = isFight
                ? new List<ToastArg> { ToastArg.Of(respondingNation), ToastArg.Of(aggressorNation) }
                : new List<ToastArg> { ToastArg.Of(respondingNation) }
        };

    /// <summary>Game paused/resumed by the host.</summary>
    public static LocalizedToast BuildPauseToast(bool isPaused) =>
        new() { Code = isPaused ? ToastCodes.GamePaused : ToastCodes.GameResumed };
}
