namespace Imperial2030.Shared.Constants;

/// <summary>
/// Resource keys for server-pushed toasts (<see cref="Models.LocalizedToast"/>). Each must have a
/// matching entry in the client's GameRoom.resx / GameRoom.be.resx.
///
/// The investment variants are spelled out as whole sentences rather than assembled from a base
/// message plus a "took control" suffix. Gluing fragments works in English but breaks in languages
/// whose word order and case endings differ — Belarusian among them — so translators get the
/// complete sentence for each case.
/// </summary>
public static class ToastCodes
{
    // {0} player, {1} nation, {2} cost, {3} previous controller
    public const string InvestmentBought = "Toast_InvestmentBought";
    public const string InvestmentBoughtTookControl = "Toast_InvestmentBoughtTookControl";
    public const string InvestmentBoughtTookControlFrom = "Toast_InvestmentBoughtTookControlFrom";

    // {0} player, {1} nation, {2} traded-in cost, {3} new cost, {4} previous controller
    public const string InvestmentUpgraded = "Toast_InvestmentUpgraded";
    public const string InvestmentUpgradedTookControl = "Toast_InvestmentUpgradedTookControl";
    public const string InvestmentUpgradedTookControlFrom = "Toast_InvestmentUpgradedTookControlFrom";

    // {0} responder, {1} nation
    public const string SwissForcedStop = "Toast_SwissForcedStop";
    public const string SwissPassed = "Toast_SwissPassed";

    public const string GamePaused = "Toast_GamePaused";
    public const string GameResumed = "Toast_GameResumed";

    // {0} responding nation, {1} aggressor nation
    public const string BattleFight = "Toast_BattleFight";
    public const string BattlePeace = "Toast_BattlePeace";
}
