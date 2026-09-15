using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// The Import rondel action. Imperial-2030-Rules.pdf p.8: a nation "may buy up to three units at 1
/// million each" from its treasury and place them "in its home provinces"; a fleet needs a harbour city;
/// a province holding a hostile foreign army cannot receive units (p.11); and the nation's unit supply
/// (<see cref="NationData.GetMaxArmies"/> / <see cref="NationData.GetMaxFleets"/>) is a hard cap.
///
/// Three copies existed. The endpoint validated the whole request and then placed everything; the bot
/// applied whatever its strategy returned, unchecked, relying on the strategy to have filtered; the
/// training server placed one unit per agent step against a legality mask and never logged the result
/// (implementation_plan.md divergences #5 and #12). All three now go through <see cref="PlaceOne"/> -
/// the batch callers via <see cref="Import"/>, which checks the request as a whole first so an invalid
/// third unit rejects the request rather than leaving two placed, and training directly, one step at a
/// time, finishing with <see cref="CompleteImport"/>.
/// </summary>
public static class ImportEngine
{
    /// <summary>
    /// Why one more <paramref name="unitType"/> could not be placed in <paramref name="territoryId"/> right
    /// now for <paramref name="nation"/>, or null if it could. <paramref name="plannedArmies"/> /
    /// <paramref name="plannedFleets"/> are units already committed by the same action but not yet on the
    /// board, so a batch is checked against the cap as a whole. Says nothing about treasury or the turn.
    /// </summary>
    public static string? PlacementRefusal(Game game, Nation nation, string territoryId, UnitType unitType, int plannedArmies = 0, int plannedFleets = 0)
    {
        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        if (territoryDef == null) return $"Invalid territory: {territoryId}";

        // Home Province Check
        if (territoryDef.Nation != nation) return $"Territory {territoryDef.Name} is not a home province of {nation}.";

        // Hostile Army Check (Standing armies of other nations block import)
        bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == territoryId && u.Nation != nation && u.UnitType == UnitType.Army && u.IsHostile);
        if (hasHostileArmy) return $"Territory {territoryDef.Name} contains hostile armies.";

        // Fleet Harbor Check
        if (unitType == UnitType.Fleet && territoryDef.CityType != CityType.LightBlue)
        {
            return $"Cannot place Fleet in {territoryDef.Name} (no harbor).";
        }

        // Unit supply
        if (unitType == UnitType.Army)
        {
            int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
            int max = NationData.GetMaxArmies(nation);
            if (currentArmies + plannedArmies + 1 > max)
                return $"Cannot import {plannedArmies + 1} armies. You already have {currentArmies} armies on the board, and the maximum allowed is {max}.";
        }
        else
        {
            int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);
            int max = NationData.GetMaxFleets(nation);
            if (currentFleets + plannedFleets + 1 > max)
                return $"Cannot import {plannedFleets + 1} fleets. You already have {currentFleets} fleets on the board, and the maximum allowed is {max}.";
        }

        return null;
    }

    public static bool CanPlace(Game game, Nation nation, string territoryId, UnitType unitType)
        => PlacementRefusal(game, nation, territoryId, unitType) == null;

    /// <summary>
    /// The checks every import shares: the game and turn state, and that the acting nation is on the
    /// Import slot and has not imported yet this turn. Returns the acting nation's state on success.
    /// </summary>
    private static string? TurnRefusal(Game game, out NationState nationState, out Player controller)
    {
        nationState = null!;
        controller = null!;
        if (game.Status != GameStatus.InProgress) return "Game not in progress.";
        if (game.IsInvestorTurn) return "Waiting for Investor Phase.";

        var ns = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        nationState = ns;
        if (ns.ControllerId == null) return "No controller.";
        controller = game.Players.First(p => p.Id == ns.ControllerId);

        if (ns.RondelPosition != RondelData.ImportSlot) return "Not in Import phase.";
        if (ns.HasImportedThisTurn) return "Already imported this turn.";
        return null;
    }

    /// <summary>
    /// Buys and places one unit for <see cref="Game.CurrentTurnNation"/>, paying
    /// <see cref="GameConstants.ImportUnitCost"/> from the treasury. Does NOT mark the import as done or
    /// log it - a step-by-step caller does that with <see cref="CompleteImport"/> once it has placed its
    /// last unit; <see cref="Import"/> does it for the batch.
    /// </summary>
    public static EngineResult PlaceOne(ApplicationDbContext? context, Game game, string territoryId, UnitType unitType)
    {
        var refusal = TurnRefusal(game, out var nationState, out _);
        if (refusal != null) return EngineResult.Fail(refusal);
        if (nationState.Treasury < GameConstants.ImportUnitCost) return EngineResult.Fail($"Insufficient treasury. Cost: {GameConstants.ImportUnitCost}M");

        refusal = PlacementRefusal(game, nationState.Nation, territoryId, unitType);
        if (refusal != null) return EngineResult.Fail(refusal);

        Place(context, game, nationState, territoryId, unitType);
        return EngineResult.Success;
    }

    private static void Place(ApplicationDbContext? context, Game game, NationState nationState, string territoryId, UnitType unitType)
    {
        var newUnit = new Unit
        {
            GameId = game.Id,
            Nation = nationState.Nation,
            TerritoryId = territoryId,
            UnitType = unitType,
            IsHostile = false, // Default to standing (friendly)
            HasMoved = false
        };
        // Both the in-memory collection (training and replay work on a disconnected game) and the
        // context, so EF tracks it as a new entity.
        game.Units.Add(newUnit);
        context?.Units.Add(newUnit);

        nationState.Treasury -= GameConstants.ImportUnitCost;
        if (context != null) context.Entry(nationState).State = EntityState.Modified;
    }

    /// <summary>
    /// Closes an import for <see cref="Game.CurrentTurnNation"/>: marks it done for the turn and logs what
    /// was placed - or that nothing was.
    /// </summary>
    public static void CompleteImport(ApplicationDbContext? context, Game game, IReadOnlyList<(UnitType UnitType, string TerritoryId)> placed)
    {
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        nationState.HasImportedThisTurn = true;
        if (context != null) context.Entry(nationState).State = EntityState.Modified;

        var playerName = controller.GetPlayerName(context);
        if (placed.Count == 0)
        {
            GameLogger.LogImportedNothing(context, game, nationState.Nation, playerName);
        }
        else
        {
            GameLogger.LogImport(context, game, placed.Count, placed, nationState.Nation, playerName);
        }
    }

    /// <summary>
    /// The whole Import action at once: checks every unit of the request against the rules and the cap as
    /// a group, then places them all, pays, marks the turn's import done and logs it. Rejects the entire
    /// request if any unit is illegal. Does not save or broadcast.
    /// </summary>
    public static EngineResult Import(ApplicationDbContext? context, Game game, IReadOnlyList<(UnitType UnitType, string TerritoryId)> units)
    {
        var refusal = TurnRefusal(game, out var nationState, out _);
        if (refusal != null) return EngineResult.Fail(refusal);

        if (units.Count > GameConstants.MaxImportUnits) return EngineResult.Fail($"Cannot import more than {GameConstants.MaxImportUnits} units.");
        if (units.Count == 0) return EngineResult.Fail("No units specified.");

        int cost = units.Count * GameConstants.ImportUnitCost;
        if (nationState.Treasury < cost) return EngineResult.Fail($"Insufficient treasury. Cost: {cost}M");

        // Validate placement - all of it before any of it, counting the request's own units toward the cap.
        int plannedArmies = 0, plannedFleets = 0;
        foreach (var (unitType, territoryId) in units)
        {
            refusal = PlacementRefusal(game, nationState.Nation, territoryId, unitType, plannedArmies, plannedFleets);
            if (refusal != null) return EngineResult.Fail(refusal);
            if (unitType == UnitType.Army) plannedArmies++; else plannedFleets++;
        }

        // Execute
        foreach (var (unitType, territoryId) in units)
        {
            Place(context, game, nationState, territoryId, unitType);
        }
        CompleteImport(context, game, units);
        return EngineResult.Success;
    }
}
