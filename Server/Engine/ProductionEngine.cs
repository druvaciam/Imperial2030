using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

public sealed record ProductionOutcome(bool Ok, string? Error = null, int ProducedCount = 0) : EngineResult(Ok, Error)
{
    public static new ProductionOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// The Production rondel action. Imperial-2030-Rules.pdf p.7: "Each armament facility and shipyard of a
/// nation may produce one army or one fleet respectively for free" - an army from a brown-city factory,
/// a fleet from a light-blue one - unless the factory city holds a hostile foreign army, and never beyond the nation's unit
/// supply (<see cref="NationData.GetMaxArmies"/> / <see cref="NationData.GetMaxFleets"/>).
///
/// An empty production still counts as the turn's action: HasProducedThisTurn is set either way, and
/// the log says which of the three things stopped it (no factory, all occupied, no piece left to place).
/// </summary>
public static class ProductionEngine
{
    public static ProductionOutcome ExecuteProduction(ApplicationDbContext? context, Game game)
    {
        if (game.Status != GameStatus.InProgress) return ProductionOutcome.Fail("Game not in progress.");
        // The nation's turn is suspended while an Investor turn resolves, so no rondel action may run.
        if (game.IsInvestorTurn) return ProductionOutcome.Fail("Waiting for Investor Phase.");

        var currentNation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == currentNation);
        if (nationState.ControllerId == null) return ProductionOutcome.Fail("No controller.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        // Check Rondel Position (Production slots: 2 and 6)
        if (!RondelData.IsProductionSlot(nationState.RondelPosition ?? -1))
        {
            return ProductionOutcome.Fail("Not on a Production slot.");
        }

        // Per-turn limit. Production is a single action taken on landing, not a repeatable one.
        if (nationState.HasProducedThisTurn) return ProductionOutcome.Fail("Already produced this turn.");

        var factoryTerritories = game.TerritoryStates
            .Where(t => t.HasFactory)
            .ToList();

        var createdUnits = 0;
        var producedDetails = new List<(UnitType UnitType, string TerritoryId)>();
        int createdArmies = 0;
        int createdFleets = 0;
        int currentArmies = game.Units.Count(u => u.Nation == currentNation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == currentNation && u.UnitType == UnitType.Fleet);

        foreach (var tState in factoryTerritories)
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tState.TerritoryId);
            if (def == null) continue;
            if (def.Nation != currentNation) continue;

            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tState.TerritoryId).ToList();
            bool isOccupied = unitsInTerritory.Any(u => u.Nation != currentNation && u.UnitType == UnitType.Army && u.IsHostile);
            if (isOccupied) continue;

            UnitType typeToProduce = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;

            if (typeToProduce == UnitType.Army && currentArmies + createdArmies >= NationData.GetMaxArmies(currentNation)) continue;
            if (typeToProduce == UnitType.Fleet && currentFleets + createdFleets >= NationData.GetMaxFleets(currentNation)) continue;

            var newUnit = new Unit
            {
                GameId = game.Id,
                Nation = currentNation,
                TerritoryId = tState.TerritoryId,
                UnitType = typeToProduce,
                IsHostile = false
            };
            // Both the in-memory collection (training and replay work on a disconnected game) and the
            // context, so EF tracks it as a new entity.
            game.Units.Add(newUnit);
            context?.Units.Add(newUnit);

            createdUnits++;
            if (typeToProduce == UnitType.Army) createdArmies++;
            else createdFleets++;
            producedDetails.Add((typeToProduce, tState.TerritoryId));
        }

        nationState.HasProducedThisTurn = true;
        if (context != null) context.Entry(nationState).State = EntityState.Modified;

        var playerName = controller.GetPlayerName(context);
        if (createdUnits > 0)
        {
            GameLogger.LogProduction(context, game, createdUnits, producedDetails, currentNation, playerName);
        }
        else
        {
            // Say which of the three things stopped it, rather than logging "produced 0 units ()".
            var ownFactories = game.TerritoryStates
                .Where(ts => ts.HasFactory && TerritoryData.AllTerritories.Any(t => t.Id == ts.TerritoryId && t.Nation == currentNation))
                .ToList();
            bool AllOccupied(TerritoryState ts) => game.Units.Any(u =>
                u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != currentNation && u.IsHostile);

            if (ownFactories.Count == 0)
            {
                GameLogger.LogProductionNoFactories(context, game, currentNation, playerName);
            }
            else if (ownFactories.All(AllOccupied))
            {
                GameLogger.LogProductionBlockaded(context, game, currentNation, playerName);
            }
            else
            {
                // Something was unoccupied and still produced nothing, so no piece of its type is left.
                GameLogger.LogProductionAtUnitCap(context, game, currentNation, playerName);
            }
        }

        return new ProductionOutcome(true, ProducedCount: createdUnits);
    }
}
