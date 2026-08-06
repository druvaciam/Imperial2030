using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using System;

namespace Imperial2030.Server.Helpers;

public static class GameLogger
{
    public static void LogAction(ApplicationDbContext? context, Game game, string message, string type, Nation? nation, string playerName)
    {
        var action = new GameAction
        {
            GameId = game.Id,
            Timestamp = DateTime.UtcNow,
            PlayerName = playerName,
            Message = message,
            ActionType = type,
            Nation = nation
        };
        game.Actions.Add(action);
        if (context != null)
        {
            context.GameActions.Add(action);
        }
    }

    public static void LogRondelMove(ApplicationDbContext? context, Game game, int targetSlot, int? currentSlot, int cost, Nation nation, string playerName)
    {
        string targetStr = Imperial2030.Shared.Constants.RondelData.GetSlotName(targetSlot);
        string fromStr = currentSlot.HasValue ? Imperial2030.Shared.Constants.RondelData.GetSlotName(currentSlot.Value) : "Start";
        
        LogAction(context, game, $"moved to {targetStr} from {fromStr} (Cost: {cost}M)", "Move", nation, playerName);
    }

    public static void LogUnitMove(ApplicationDbContext? context, Game game, UnitType unitType, string originName, string targetName, bool isHostileMove, Nation nation, string playerName)
    {
        string typeStr = unitType == UnitType.Fleet ? "fleet" : "army";
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";

        LogAction(context, game, $"{typeStr} moved to {targetName} from {originName} (Hostile: {isHostileMove})", actionType, nation, playerName);
    }
    public static void LogUnitMoveAwaitingResponse(ApplicationDbContext? context, Game game, UnitType unitType, string originName, string targetName, bool isHostileMove, string defendersStr, Nation nation, string playerName)
    {
        string typeStr = unitType == UnitType.Fleet ? "fleet" : "army";
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        string peaceOrHostile = isHostileMove ? "hostilely" : "peacefully";

        LogAction(context, game, $"{typeStr} moved {peaceOrHostile} to {targetName} from {originName}, awaiting response from {defendersStr}", actionType, nation, playerName);
    }
    
    public static void LogInvestmentBuy(ApplicationDbContext? context, Game game, Nation nation, int cost, string playerName, string? controlChangeMessage = null)
    {
        string message = $"bought {nation} {cost}M bond";
        if (!string.IsNullOrEmpty(controlChangeMessage))
        {
            message += controlChangeMessage;
        }
        LogAction(context, game, message, "Investment", null, playerName);
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
        var locationNames = units.Select(u => $"{u.UnitType} in " + (Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId));
        LogAction(context, game, $"imported {importedCount} units ({string.Join(", ", locationNames)})", "Import", nation, playerName);
    }

    public static void LogProduction(ApplicationDbContext? context, Game game, int producedCount, IEnumerable<(UnitType UnitType, string TerritoryId)> units, Nation nation, string playerName)
    {
        var locationNames = units.Select(u => $"{u.UnitType} in " + (Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId));
        LogAction(context, game, $"produced {producedCount} units ({string.Join(", ", locationNames)})", "Production", nation, playerName);
    }
    

}
