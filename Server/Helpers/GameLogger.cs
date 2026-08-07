using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using System;
using Imperial2030.Shared.Constants;

namespace Imperial2030.Server.Helpers;

public static class GameLogger
{
    public static void LogAction(ApplicationDbContext? context, Game game, string message, string type, Nation? nation, string playerName, object? metadata = null)
    {
        var action = new GameAction
        {
            GameId = game.Id,
            Timestamp = DateTime.UtcNow,
            PlayerName = playerName,
            Message = message,
            ActionType = type,
            Nation = nation,
            Metadata = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : string.Empty
        };
        game.Actions.Add(action);
        if (context != null)
        {
            context.GameActions.Add(action);
        }
    }

    public static void LogRondelMove(ApplicationDbContext? context, Game game, int targetSlot, int? currentSlot, int cost, Nation nation, string playerName)
    {
        string targetStr = RondelData.GetSlotName(targetSlot);
        string fromStr = currentSlot.HasValue ? RondelData.GetSlotName(currentSlot.Value) : "Start";

        LogAction(context, game, $"moved to {targetStr} from {fromStr} (Cost: {cost}M)", "Move", nation, playerName);
    }

    public static void LogUnitMove(ApplicationDbContext? context, Game game, UnitType unitType, string originId, string targetId, bool isHostileMove, Nation nation, string playerName)
    {
        string typeStr = unitType == UnitType.Fleet ? "fleet" : "army";
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";

        string originName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == originId)?.Name ?? originId;
        string targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == targetId)?.Name ?? targetId;

        LogAction(context, game, $"{typeStr} moved to {targetName} from {originName} (Hostile: {isHostileMove})", actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId });
    }
    public static void LogUnitMoveAwaitingResponse(ApplicationDbContext? context, Game game, UnitType unitType, string originId, string targetId, bool isHostileMove, string defendersStr, Nation nation, string playerName)
    {
        string typeStr = unitType == UnitType.Fleet ? "fleet" : "army";
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        string peaceOrHostile = isHostileMove ? "hostilely" : "peacefully";

        string originName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == originId)?.Name ?? originId;
        string targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == targetId)?.Name ?? targetId;

        LogAction(context, game, $"{typeStr} moved {peaceOrHostile} to {targetName} from {originName}, awaiting response from {defendersStr}", actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId });
    }

    public static void LogBattleDestruction(ApplicationDbContext? context, Game game, UnitType attackerType, Nation targetNation, UnitType defenderType, string territoryId, Nation nation, string playerName)
    {
        string targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)?.Name ?? territoryId;
        LogAction(context, game, $"{attackerType.ToString().ToLower()} attacked {targetNation} {defenderType.ToString().ToLower()} in {targetName}. Both destroyed", "Battle", nation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = nation, DefenderNation = targetNation, UnitType = attackerType, DefenderUnitType = defenderType });
    }

    public static void LogBattleResponseDestruction(ApplicationDbContext? context, Game game, Nation respondingNation, UnitType responderType, Nation aggressorNation, UnitType aggressorType, string territoryId, string playerName)
    {
        string targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)?.Name ?? territoryId;
        LogAction(context, game, $"{respondingNation} {responderType.ToString().ToLower()} chose FIGHT against {aggressorNation} {aggressorType.ToString().ToLower()} in {targetName}. Both destroyed", "Battle", respondingNation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = aggressorNation, DefenderNation = respondingNation, UnitType = aggressorType, DefenderUnitType = responderType });
    }

    public static void LogTerritoryControlChange(ApplicationDbContext? context, Game game, string territoryName, Nation? oldController, Nation newController, string playerName)
    {
        string msg = oldController.HasValue
            ? $"took control of {territoryName} from {oldController.Value}"
            : $"took control of {territoryName}";

        LogAction(context, game, msg, "FlagPlacement", newController, playerName);
    }

    public static void LogFactoryBuild(ApplicationDbContext? context, Game game, string cityName, Nation nation, string playerName)
    {
        LogAction(context, game, $"built a factory in {cityName}", "Factory", nation, playerName);
    }

    public static void LogFactoryDestruction(ApplicationDbContext? context, Game game, string territoryId, Nation nation, string playerName)
    {
        string territoryName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)?.Name ?? territoryId;
        LogAction(context, game, $"destroyed {nation} factory in {territoryName}", "DestroyFactory", nation, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogSwissBankForceStop(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, $"chose to FORCE STOP {nation} on Investor", "SwissBankResponse", nation, playerName);
    }

    public static void LogSwissBankPass(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, $"chose to PASS on forcing {nation} to stop", "SwissBankResponse", nation, playerName);
    }

    public static void LogBattleResponsePeace(ApplicationDbContext? context, Game game, Nation respondingNation, Nation aggressorNation, string territoryName, string playerName)
    {
        LogAction(context, game, $"{respondingNation} agreed to PEACE with {aggressorNation} in {territoryName}", "BattleResponse", respondingNation, playerName);
    }

    public static void LogHostilityToggle(ApplicationDbContext? context, Game game, UnitType unitType, string territoryName, bool isHostile, Nation nation, string playerName)
    {
        string typeStr = unitType.ToString().ToLower();
        string statusStr = isHostile ? "hostile" : "friendly";
        LogAction(context, game, $"{typeStr} in {territoryName} converted to {statusStr}", "ToggleHostility", nation, playerName);
    }

    public static void LogInvestmentBuy(ApplicationDbContext? context, Game game, Nation nation, int cost, string playerName, object? metadata = null)
    {
        string message = $"bought {nation} {cost}M bond";
        LogAction(context, game, message, "Investment", null, playerName, metadata);
    }

    public static void LogInvestmentPass(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "passed on investment", "Investment", null, playerName);
    }

    public static void LogTaxation(ApplicationDbContext? context, Game game, int totalRevenue, int soldiersPay, int treasuryGain, int bonus, int powerGain, Nation nation, string playerName)
    {
        string soldiersPayStr = soldiersPay > 0 ? $"-{soldiersPay}" : soldiersPay.ToString();
        string tGainStr = treasuryGain > 0 ? $"+{treasuryGain}" : treasuryGain.ToString();
        string bonusStr = bonus > 0 ? $"+{bonus}" : bonus.ToString();
        string powerStr = powerGain > 0 ? $"+{powerGain}" : powerGain.ToString();
        LogAction(context, game, $"collected taxes: {totalRevenue}M (Soldiers' Pay: {soldiersPayStr}M, Treasury Gain: {tGainStr}M, Bonus: {bonusStr}M, Power: {powerStr})", "Taxation", nation, playerName);
    }

    public static void LogImport(ApplicationDbContext? context, Game game, int importedCount, IEnumerable<(UnitType UnitType, string TerritoryId)> units, Nation nation, string playerName)
    {
        var locationNames = units.Select(u => $"{u.UnitType} in " + (TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId));
        LogAction(context, game, $"imported {importedCount} units ({string.Join(", ", locationNames)})", "Import", nation, playerName);
    }

    public static void LogAutoEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        LogAction(context, game, $"auto-ended {phaseName} maneuver phase", "NextPhase", nation, playerName);
    }

    public static void LogEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        LogAction(context, game, $"ended {phaseName} maneuver phase", "NextPhase", nation, playerName);
    }

    public static void LogProduction(ApplicationDbContext? context, Game game, int producedCount, IEnumerable<(UnitType UnitType, string TerritoryId)> units, Nation nation, string playerName)
    {
        var locationNames = units.Select(u => $"{u.UnitType} in " + (TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId));
        LogAction(context, game, $"produced {producedCount} units ({string.Join(", ", locationNames)})", "Production", nation, playerName);
    }


}
