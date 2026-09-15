using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Building a factory. Imperial-2030-Rules.pdf p.7: "The nation pays 5 million into the bank... a factory
/// may be built in one of the nation's home cities. Only one factory may be built in each city", and
/// p.11: a city holding a hostile foreign army cannot be built in.
/// </summary>
public static class FactoryEngine
{
    /// <summary>
    /// Whether <paramref name="territoryId"/> is a place <paramref name="nation"/> could build in right
    /// now: one of its home cities, with no factory yet and no hostile foreign army standing in it. Says
    /// nothing about whether the nation may build at all this turn (rondel slot, treasury, once per turn) -
    /// that is <see cref="BuildFactory"/>'s job. Used by the bot and the RL action mask to enumerate
    /// candidate cities.
    /// </summary>
    public static bool IsBuildableSite(Game game, Nation nation, string territoryId)
        => SiteRefusal(game, nation, TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)) == null;

    /// <summary>The nation's home cities where <see cref="IsBuildableSite"/> holds, in map order.</summary>
    public static List<Territory> BuildableCities(Game game, Nation nation)
        => TerritoryData.AllTerritories
            .Where(t => t.IsHomeCity(nation) && SiteRefusal(game, nation, t) == null)
            .ToList();

    private static string? SiteRefusal(Game game, Nation nation, Territory? territoryDef)
    {
        if (territoryDef == null) return "Invalid territory.";
        if (!territoryDef.IsHomeCity(nation)) return $"Can only build in {nation}'s home cities.";

        var territoryState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == territoryDef.Id);
        if (territoryState != null && territoryState.HasFactory) return "Factory already exists.";

        bool hasHostileForeignArmy = game.Units.Any(u => u.TerritoryId == territoryDef.Id && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
        if (hasHostileForeignArmy) return "Cannot build factory: hostile foreign armies are present in the city.";

        return null;
    }

    /// <summary>
    /// Builds a factory for <see cref="Game.CurrentTurnNation"/> in <paramref name="territoryId"/>: pays
    /// <see cref="GameConstants.FactoryCost"/> from the nation's treasury, places the factory, marks the
    /// nation as having built this turn, and logs it. Does not save or broadcast.
    /// </summary>
    public static EngineResult BuildFactory(ApplicationDbContext? context, Game game, string territoryId)
    {
        if (game.Status != GameStatus.InProgress) return EngineResult.Fail("Game not in progress.");
        if (game.IsInvestorTurn) return EngineResult.Fail("Waiting for Investor Phase.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return EngineResult.Fail("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        // 1. Validate Rondel Position
        if (nationState.RondelPosition != RondelData.FactorySlot) return EngineResult.Fail("Nation must be on 'Factory' slot.");

        // 1b. Validate Per Turn Limit
        if (nationState.HasBuiltThisTurn) return EngineResult.Fail("Already built factory this turn.");

        // 2-4b. Validate the site: exists, is a home city, has no factory, holds no hostile foreign army
        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        var refusal = SiteRefusal(game, nation, territoryDef);
        if (refusal != null) return EngineResult.Fail(refusal);

        // 5. Validate Cost (5M from Nation Treasury - p.7 "The nation pays 5 million into the bank")
        const int FactoryCost = GameConstants.FactoryCost;
        if (nationState.Treasury < FactoryCost) return EngineResult.Fail($"Nation treasury insufficient. Need {FactoryCost}M.");

        // 6. Execute Build. Setup creates a TerritoryState for every territory; a missing one is created
        // rather than refused.
        var territoryState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == territoryId);
        bool createdState = territoryState == null;
        if (territoryState == null)
        {
            territoryState = new TerritoryState { TerritoryId = territoryId, GameId = game.Id };
            game.TerritoryStates.Add(territoryState);
            context?.TerritoryStates.Add(territoryState);
        }

        nationState.Treasury -= FactoryCost;
        territoryState.HasFactory = true;

        // Set flag. The turn does not advance here: Factory is the action of a turn whose rondel move
        // already happened, and ending the turn is the caller's separate step.
        nationState.HasBuiltThisTurn = true;

        if (context != null)
        {
            context.Entry(nationState).State = EntityState.Modified;
            // A state created just above is already tracked as Added; forcing Modified would make EF
            // issue an UPDATE for a row that does not exist yet.
            if (!createdState) context.Entry(territoryState).State = EntityState.Modified;
        }

        GameLogger.LogFactoryBuild(context, game, territoryDef!.Name, nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }
}
