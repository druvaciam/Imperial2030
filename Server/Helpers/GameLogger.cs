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
        LogAction(context, game, "", "Move", nation, playerName, new RondelMoveMetadata { TargetSlot = targetSlot, CurrentSlot = currentSlot, Cost = cost });
    }

    public static void LogUnitMove(ApplicationDbContext? context, Game game, UnitType unitType, string originId, string targetId, bool isHostileMove, Nation nation, string playerName)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, "", actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId, IsHostileMove = isHostileMove });
    }

    public static void LogUnitStay(ApplicationDbContext? context, Game game, UnitType unitType, string territoryId, Nation nation, string playerName)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, "", actionType, nation, playerName, new ActionMetadata { FromTerritoryId = territoryId, ToTerritoryId = territoryId });
    }
    public static void LogUnitMoveAwaitingResponse(ApplicationDbContext? context, Game game, UnitType unitType, string originId, string targetId, bool isHostileMove, string defendersStr, Nation nation, string playerName)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, "", actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId, IsHostileMove = isHostileMove, DefendersStr = defendersStr });
    }

    public static void LogBattleDestruction(ApplicationDbContext? context, Game game, UnitType attackerType, Nation targetNation, UnitType defenderType, string territoryId, Nation nation, string playerName)
    {
        LogAction(context, game, "", "Battle", nation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = nation, DefenderNation = targetNation, UnitType = attackerType, DefenderUnitType = defenderType, IsResponse = false });
    }

    public static void LogBattleResponseDestruction(ApplicationDbContext? context, Game game, Nation respondingNation, UnitType responderType, Nation aggressorNation, UnitType aggressorType, string territoryId, string playerName)
    {
        LogAction(context, game, "", "Battle", respondingNation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = aggressorNation, DefenderNation = respondingNation, UnitType = aggressorType, DefenderUnitType = responderType, IsResponse = true });
    }

    public static void LogTerritoryControlChange(ApplicationDbContext? context, Game game, string territoryName, Nation? oldController, Nation newController, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        LogAction(context, game, "", "FlagPlacement", newController, playerName, new FlagPlacementMetadata { TerritoryId = territoryId, OldController = oldController, NewController = newController });
    }

    public static void LogFactoryBuild(ApplicationDbContext? context, Game game, string cityName, Nation nation, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == cityName)?.Id ?? cityName;
        LogAction(context, game, "", "Factory", nation, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogFactoryDestruction(ApplicationDbContext? context, Game game, string territoryId, Nation nation, string playerName)
    {
        LogAction(context, game, "", "DestroyFactory", nation, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogSwissBankForceStop(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, "", "SwissBankResponse", nation, playerName, new SwissBankResponseMetadata { IsForceStop = true, Nation = nation });
    }

    public static void LogSwissBankPass(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, "", "SwissBankResponse", nation, playerName, new SwissBankResponseMetadata { IsForceStop = false, Nation = nation });
    }

    public static void LogBattleResponsePeace(ApplicationDbContext? context, Game game, Nation respondingNation, Nation aggressorNation, string territoryName, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        LogAction(context, game, "", "BattleResponse", respondingNation, playerName, new ActionMetadata { TerritoryId = territoryId, RespondingNationStr = respondingNation.ToString(), AggressorNation = aggressorNation });
    }

    public static void LogAllPartiesPeace(ApplicationDbContext? context, Game game, string territoryId, string playerName)
    {
        LogAction(context, game, "", "AllPartiesPeace", null, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogHostilityToggle(ApplicationDbContext? context, Game game, UnitType unitType, string territoryName, bool isHostile, Nation nation, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        LogAction(context, game, "", "ToggleHostility", nation, playerName, new HostilityMetadata { UnitType = unitType, TerritoryId = territoryId, IsHostile = isHostile });
    }

    public static void LogInvestmentBuy(ApplicationDbContext? context, Game game, Nation nation, int cost, string playerName, string? newControllerName = null, string? oldControllerName = null, bool isSwissBankKicked = false, int? tradeInCost = null)
    {
        var metadata = new InvestmentMetadata
        {
            NewControllerName = newControllerName,
            OldControllerName = oldControllerName,
            IsSwissBankKicked = isSwissBankKicked,
            Nation = nation.ToString(),
            Cost = cost,
            TradeInCost = tradeInCost
        };
        LogAction(context, game, "", "Investment", null, playerName, metadata);
    }

    public static void LogInvestmentPass(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "", "Investment", null, playerName);
    }

    public static void LogTaxation(ApplicationDbContext? context, Game game, int totalRevenue, int soldiersPay, int treasuryGain, int bonus, int powerGain, Nation nation, string playerName)
    {
        var metadata = new TaxationMetadata
        {
            TotalRevenue = totalRevenue,
            SoldiersPay = soldiersPay,
            TreasuryGain = treasuryGain,
            Bonus = bonus,
            PowerGain = powerGain
        };
        LogAction(context, game, "", "Taxation", nation, playerName, metadata);
    }

    public static void LogImport(ApplicationDbContext? context, Game game, int importedCount, IEnumerable<(UnitType UnitType, string TerritoryId)> units, Nation nation, string playerName)
    {
        var metadata = new ImportMetadata
        {
            ImportedCount = importedCount,
            Units = units.Select(u => new ImportUnitInfo
            {
                UnitType = u.UnitType,
                TerritoryId = u.TerritoryId,
                TerritoryName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId
            }).ToList()
        };
        LogAction(context, game, "", "Import", nation, playerName, metadata);
    }

    public static void LogAutoSkipManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "", "AutoSkipPhase", nation, playerName, metadata);
    }

    public static void LogAutoEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "", "AutoEndPhase", nation, playerName, metadata);
    }

    public static void LogEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "", "EndPhase", nation, playerName, metadata);
    }

    public static void LogProduction(ApplicationDbContext? context, Game game, int producedCount, IEnumerable<(UnitType UnitType, string TerritoryId)> units, Nation nation, string playerName)
    {
        var metadata = new ProductionMetadata
        {
            ProducedCount = producedCount,
            Units = units.Select(u => new ProductionUnitInfo
            {
                UnitType = u.UnitType,
                TerritoryId = u.TerritoryId,
                TerritoryName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == u.TerritoryId)?.Name ?? u.TerritoryId
            }).ToList()
        };
        LogAction(context, game, "", "Production", nation, playerName, metadata);
    }
}
