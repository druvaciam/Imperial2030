using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using System;
using System.Collections.Generic;
using Imperial2030.Shared.Constants;

namespace Imperial2030.Server.Helpers;

public static class GameLogger
{
    private static void LogAction(ApplicationDbContext? context, Game game, string type, Nation? nation, string playerName, object? metadata = null)
    {
        long nextIndex = 1;
        if (game.Actions != null && game.Actions.Count > 0)
        {
            nextIndex = game.Actions.Max(a => a.OrderIndex) + 1;
        }
        else if (context != null)
        {
            var maxDb = context.GameActions.Where(a => a.GameId == game.Id).Select(a => (long?)a.OrderIndex).Max();
            nextIndex = (maxDb ?? 0) + 1;
        }

        var action = new GameAction
        {
            GameId = game.Id,
            OrderIndex = nextIndex,
            Timestamp = DateTime.UtcNow,
            PlayerName = playerName,
            Message = string.Empty,
            ActionType = type,
            Nation = nation,
            Metadata = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : string.Empty
        };
        game.Actions?.Add(action);
        context?.GameActions.Add(action);
    }

    public static void LogRondelMove(ApplicationDbContext? context, Game game, int targetSlot, int? currentSlot, int cost, Nation nation, string playerName)
    {
        LogAction(context, game, "Move", nation, playerName, new RondelMoveMetadata { TargetSlot = targetSlot, CurrentSlot = currentSlot, Cost = cost });
    }

    /// <param name="routeVia">
    /// Every territory the unit passed through, in travel order, excluding origin and destination -
    /// rail hops and convoy sea regions alike. Null or empty for a plain step to an adjacent territory.
    /// Only the caller performing the move knows this; it cannot be worked out from the log afterwards,
    /// so anything replaying or drawing the move has to read it from here rather than guess.
    /// </param>
    public static void LogUnitMove(ApplicationDbContext? context, Game game, UnitType unitType, bool sourceIsHostile, string originId, string targetId, bool isHostileMove, Nation nation, string playerName, List<string>? routeVia = null)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId, IsHostileMove = isHostileMove, SourceIsHostile = sourceIsHostile, RouteVia = routeVia });
    }

    public static void LogUnitStay(ApplicationDbContext? context, Game game, UnitType unitType, bool sourceIsHostile, string territoryId, Nation nation, string playerName)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, actionType, nation, playerName, new ActionMetadata { FromTerritoryId = territoryId, ToTerritoryId = territoryId, SourceIsHostile = sourceIsHostile });
    }
    /// <param name="routeVia">See LogUnitMove - same reason, for a move that opens a battle negotiation.</param>
    public static void LogUnitMoveAwaitingResponse(ApplicationDbContext? context, Game game, UnitType unitType, bool sourceIsHostile, string originId, string targetId, bool isHostileMove, string defendersStr, Nation nation, string playerName, List<string>? routeVia = null)
    {
        string actionType = unitType == UnitType.Fleet ? "MoveFleet" : "MoveArmy";
        LogAction(context, game, actionType, nation, playerName, new ActionMetadata { FromTerritoryId = originId, ToTerritoryId = targetId, IsHostileMove = isHostileMove, DefendersStr = defendersStr, SourceIsHostile = sourceIsHostile, RouteVia = routeVia });
    }

    public static void LogBattleDestruction(ApplicationDbContext? context, Game game, UnitType attackerType, Nation targetNation, UnitType defenderType, string territoryId, Nation nation, string playerName)
    {
        LogAction(context, game, "Battle", nation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = nation, DefenderNation = targetNation, UnitType = attackerType, DefenderUnitType = defenderType, IsResponse = false });
    }

    public static void LogBattleResponseDestruction(ApplicationDbContext? context, Game game, Nation respondingNation, UnitType responderType, Nation aggressorNation, UnitType aggressorType, string territoryId, string playerName)
    {
        LogAction(context, game, "Battle", respondingNation, playerName, new ActionMetadata { TerritoryId = territoryId, AggressorNation = aggressorNation, DefenderNation = respondingNation, UnitType = aggressorType, DefenderUnitType = responderType, IsResponse = true });
    }

    public static void LogTerritoryControlChange(ApplicationDbContext? context, Game game, string territoryName, Nation? oldController, Nation? newController, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        Nation? affectedNation = newController ?? oldController;
        string actualPlayerName = playerName;

        if (affectedNation.HasValue)
        {
            var controllerId = game.NationStates.FirstOrDefault(ns => ns.Nation == affectedNation.Value)?.ControllerId;
            var controller = game.Players.FirstOrDefault(p => p.Id == controllerId);
            if (controller != null)
            {
                // GetPlayerName checks BotName before IsBot. The previous inline IsBot-gated lookup fell
                // through to the AspNetUsers row during replay/import (where players are deliberately kept
                // IsBot = false), stamping the throwaway "import-<guid>" account name into the log instead
                // of the real player — which also made an imported game's log differ from the original's.
                actualPlayerName = controller.GetPlayerName(context);
            }
        }

        LogAction(context, game, "FlagPlacement", newController, actualPlayerName, new FlagPlacementMetadata { TerritoryId = territoryId, OldController = oldController, NewController = newController });
    }

    public static void LogFactoryBuild(ApplicationDbContext? context, Game game, string cityName, Nation nation, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == cityName)?.Id ?? cityName;
        LogAction(context, game, "Factory", nation, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogFactoryDestruction(ApplicationDbContext? context, Game game, string territoryId, Nation nation, string playerName)
    {
        LogAction(context, game, "DestroyFactory", nation, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogSwissBankForceStop(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, "SwissBankResponse", nation, playerName, new SwissBankResponseMetadata { IsForceStop = true, Nation = nation });
    }

    public static void LogSwissBankPass(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, "SwissBankResponse", nation, playerName, new SwissBankResponseMetadata { IsForceStop = false, Nation = nation });
    }

    public static void LogBattleResponsePeace(ApplicationDbContext? context, Game game, Nation respondingNation, Nation aggressorNation, string territoryName, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        LogAction(context, game, "BattleResponse", respondingNation, playerName, new ActionMetadata { TerritoryId = territoryId, RespondingNationStr = respondingNation.ToString(), AggressorNation = aggressorNation });
    }

    public static void LogAllPartiesPeace(ApplicationDbContext? context, Game game, string territoryId, string playerName)
    {
        LogAction(context, game, "AllPartiesPeace", null, playerName, new ActionMetadata { TerritoryId = territoryId });
    }

    public static void LogHostilityToggle(ApplicationDbContext? context, Game game, UnitType unitType, string territoryName, bool isHostile, Nation nation, string playerName)
    {
        string territoryId = TerritoryData.AllTerritories.FirstOrDefault(t => t.Name == territoryName)?.Id ?? territoryName;
        LogAction(context, game, "ToggleHostility", nation, playerName, new HostilityMetadata { UnitType = unitType, TerritoryId = territoryId, IsHostile = isHostile });
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
        LogAction(context, game, "Investment", null, playerName, metadata);
    }

    public static void LogInvestmentPass(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "Investment", null, playerName);
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
        LogAction(context, game, "Taxation", nation, playerName, metadata);
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
        LogAction(context, game, "Import", nation, playerName, metadata);
    }

    public static void LogAutoSkipManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "AutoSkipPhase", nation, playerName, metadata);
    }

    public static void LogAutoEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "AutoEndPhase", nation, playerName, metadata);
    }

    public static void LogEndManeuverPhase(ApplicationDbContext? context, Game game, string phaseName, Nation nation, string playerName)
    {
        var metadata = new PhaseMetadata { PhaseName = phaseName };
        LogAction(context, game, "EndPhase", nation, playerName, metadata);
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
        LogAction(context, game, "Production", nation, playerName, metadata);
    }

    public static void LogInvestorInterestPaid(ApplicationDbContext? context, Game game, Nation nation, string controllerName, int paidAmount, string payeeName)
    {
        LogAction(context, game, "Investor", nation, controllerName, new InvestorMetadata { Type = "InterestPaid", PaidAmount = paidAmount, PayeeName = payeeName });
    }

    public static void LogInvestorInterestPartial(ApplicationDbContext? context, Game game, Nation nation, string controllerName, int paidAmount, int expectedAmount, string payeeName)
    {
        LogAction(context, game, "Investor", nation, controllerName, new InvestorMetadata { Type = "InterestPartial", PaidAmount = paidAmount, ExpectedAmount = expectedAmount, PayeeName = payeeName });
    }

    public static void LogInvestorUnableToPay(ApplicationDbContext? context, Game game, Nation nation, string controllerName, int expectedAmount, string payeeName, bool treasuryEmpty, bool missedInterest)
    {
        LogAction(context, game, "Investor", nation, controllerName, new InvestorMetadata { Type = "UnableToPay", ExpectedAmount = expectedAmount, PayeeName = payeeName, MissedInterest = missedInterest, TreasuryEmpty = treasuryEmpty });
    }

    public static void LogInvestorPersonallyContributed(ApplicationDbContext? context, Game game, Nation nation, string controllerName, int personalContribution)
    {
        LogAction(context, game, "Investor", nation, controllerName, new InvestorMetadata { Type = "PersonallyContributed", PersonalContribution = personalContribution });
    }

    public static void LogInvestorBonus(ApplicationDbContext? context, Game game, string playerName, int paidAmount)
    {
        LogAction(context, game, "InvestorBonus", null, playerName, new InvestorMetadata { Type = "InvestorBonus", PaidAmount = paidAmount });
    }

    public static void LogJoinGame(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "JoinGame", null, playerName);
    }

    public static void LogLeaveGame(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "LeaveGame", null, playerName);
    }

    public static void LogStartGame(ApplicationDbContext? context, Game game, string playerName, Dictionary<Nation, Guid>? nationDistribution = null, List<PlayerRosterEntry>? roster = null)
    {
        GameSetupMetadata? metadata = null;
        if (nationDistribution != null)
        {
            metadata = new GameSetupMetadata
            {
                NationDistribution = nationDistribution,
                Players = roster ?? new List<PlayerRosterEntry>(),
                MaxPlayers = game.MaxPlayers,
                IsPrivate = game.IsPrivate,
                VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases
            };
        }
        LogAction(context, game, "StartGame", null, playerName, metadata);
    }

    public static void LogPauseGame(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "PauseGame", null, playerName);
    }

    public static void LogResumeGame(ApplicationDbContext? context, Game game, string playerName)
    {
        LogAction(context, game, "ResumeGame", null, playerName);
    }

    public static void LogEndTurn(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        LogAction(context, game, "EndTurn", nation, playerName);
    }
}
