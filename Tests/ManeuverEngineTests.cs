using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The maneuver operations pinned directly against the engine, Imperial-2030-Rules.pdf p.8-p.11.
/// </summary>
public class ManeuverEngineTests
{
    private static (Game Game, Player Gov, Player Rival) BuildGame(Nation nation, ManeuverPhase phase)
    {
        var gov = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Gov" };
        var rival = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Rival" };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Maneuver",
            Status = GameStatus.InProgress,
            CurrentTurnNation = nation,
            CurrentManeuverPhase = phase,
            Players = new List<Player> { gov, rival },
            NationStates = Enum.GetValues<Nation>()
                .Select(n => new NationState { Nation = n, ControllerId = n == nation ? gov.Id : rival.Id, RondelPosition = RondelData.ManeuverSlot1 })
                .ToList(),
            Units = new List<Unit>(),
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };
        return (game, gov, rival);
    }

    private static Unit Add(Game game, Nation nation, UnitType type, string territoryId, bool hostile = false)
    {
        var u = new Unit { Id = Guid.NewGuid(), Nation = nation, UnitType = type, TerritoryId = territoryId, IsHostile = hostile };
        game.Units.Add(u);
        return u;
    }

    private static string SeaNextTo(string harborId) =>
        MapConnectivity.Adjacency[harborId].First(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea));

    private static string LandNextTo(string territoryId, Nation? notHomeOf = null) =>
        MapConnectivity.Adjacency[territoryId].First(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Land && (notHomeOf == null || t.Nation != notHomeOf)));

    // ---- fleets (p.8) --------------------------------------------------------------------------

    [Fact]
    public void AFleetLeavesHarborForTheAdjacentSeaAndMayNotReturnToLand()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var harbor = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.LightBlue).Id;
        var fleet = Add(game, Nation.Russia, UnitType.Fleet, harbor);
        var sea = SeaNextTo(harbor);

        var result = ManeuverEngine.MoveFleet(null, game, fleet.Id, sea, isHostile: false);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(sea, fleet.TerritoryId);
        Assert.True(fleet.HasMoved);
        Assert.Single(game.Actions, a => a.ActionType == "MoveFleet");

        var land = Add(game, Nation.Russia, UnitType.Fleet, sea);
        var landNeighbor = MapConnectivity.Adjacency[sea].First(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Land));
        Assert.Equal("Fleets can only move into sea regions.", ManeuverEngine.MoveFleet(null, game, land.Id, landNeighbor, false).Error);
    }

    [Fact]
    public void AFleetArrivingPeacefullyAmongForeignFleetsAsksThemWhetherTheyFight()
    {
        // p.8: "If the invader wants to stay friendly, however, he has to offer the opportunity for a battle
        // to each other fleet present in the sea region."
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var harbor = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.LightBlue).Id;
        var sea = SeaNextTo(harbor);
        var fleet = Add(game, Nation.Russia, UnitType.Fleet, harbor);
        Add(game, Nation.China, UnitType.Fleet, sea);

        var result = ManeuverEngine.MoveFleet(null, game, fleet.Id, sea, isHostile: false);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.BattlePending);
        Assert.Equal(sea, game.PendingBattleTerritoryId);
        Assert.Equal(new[] { Nation.China }, game.PendingBattleDefenders);
        Assert.Equal(2, game.Units.Count); // nothing destroyed yet
    }

    [Fact]
    public void AHostileFleetAgainstOneForeignFleetFightsOnTheSpot()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var harbor = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.LightBlue).Id;
        var sea = SeaNextTo(harbor);
        var fleet = Add(game, Nation.Russia, UnitType.Fleet, harbor);
        Add(game, Nation.China, UnitType.Fleet, sea);

        var result = ManeuverEngine.MoveFleet(null, game, fleet.Id, sea, isHostile: true);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.MoverDestroyed);
        Assert.Equal(Nation.China, result.DefeatedNation);
        Assert.Empty(game.Units);
        Assert.Contains(game.Actions, a => a.ActionType == "Battle");
    }

    // ---- armies (p.9-10) ------------------------------------------------------------------------

    [Fact]
    public void AnArmyStepsToAnAdjacentLandRegionAndNotOntoTheSea()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var home = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.Brown).Id;
        var army = Add(game, Nation.Russia, UnitType.Army, home);
        var next = LandNextTo(home);

        var result = ManeuverEngine.MoveArmy(null, game, army.Id, next, isHostile: false);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(next, army.TerritoryId);
        Assert.Null(result.RouteVia);

        var second = Add(game, Nation.Russia, UnitType.Army, home);
        var sea = MapConnectivity.Adjacency[home].FirstOrDefault(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea));
        if (sea != null)
            Assert.Equal("Armies can only move on land.", ManeuverEngine.MoveArmy(null, game, second.Id, sea, false).Error);
    }

    [Fact]
    public void TheLastUnoccupiedFactoryProvinceMayNotBeEnteredHostilelyEvenToFight()
    {
        // p.10: "the province of this factory may not be entered by hostile armies. Armies of other nations
        // that enter this province are laid down on their sides." A defender being present changes nothing.
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var chinaCity = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.China) && t.CityType == CityType.Brown);
        game.TerritoryStates.First(t => t.TerritoryId == chinaCity.Id).HasFactory = true; // China's only factory
        var from = LandNextTo(chinaCity.Id, notHomeOf: Nation.China);
        var army = Add(game, Nation.Russia, UnitType.Army, from);
        Add(game, Nation.China, UnitType.Army, chinaCity.Id);

        var hostile = ManeuverEngine.MoveArmy(null, game, army.Id, chinaCity.Id, isHostile: true);
        Assert.False(hostile.Ok);
        Assert.Equal("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.", hostile.Error);
        Assert.Equal(from, army.TerritoryId);
        Assert.False(army.HasMoved);

        var peaceful = ManeuverEngine.MoveArmy(null, game, army.Id, chinaCity.Id, isHostile: false);
        Assert.True(peaceful.Ok, peaceful.Error);
        Assert.True(peaceful.BattlePending); // the defender may still call for a battle
        Assert.False(army.IsHostile);
    }

    [Fact]
    public void ForeignUnitsInYourOwnHomeProvinceAreAlwaysMetHostilely()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var home = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.Brown).Id;
        var from = LandNextTo(home);
        var army = Add(game, Nation.Russia, UnitType.Army, from);
        Add(game, Nation.China, UnitType.Army, home, hostile: true);

        var result = ManeuverEngine.MoveArmy(null, game, army.Id, home, isHostile: false);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.MoverDestroyed);
        Assert.Equal(Nation.China, result.DefeatedNation);
        Assert.Empty(game.Units);
    }

    [Fact]
    public void AUnitAlreadyMovedOrInTheWrongPhaseIsRefused()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var home = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.Brown).Id;
        var army = Add(game, Nation.Russia, UnitType.Army, home);

        Assert.Equal("Not in Army Maneuver phase.", ManeuverEngine.MoveArmy(null, game, army.Id, LandNextTo(home), false).Error);

        game.CurrentManeuverPhase = ManeuverPhase.Armies;
        army.HasMoved = true;
        Assert.Equal("Unit already moved.", ManeuverEngine.MoveArmy(null, game, army.Id, LandNextTo(home), false).Error);

        var foreign = Add(game, Nation.China, UnitType.Army, home);
        Assert.Equal("Not your unit.", ManeuverEngine.MoveArmy(null, game, foreign.Id, LandNextTo(home), false).Error);
    }

    // ---- hostility and stationary battles (p.10) -----------------------------------------------

    [Fact]
    public void StandingUpInTheLastUnoccupiedFactoryProvinceIsRefused()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var chinaCity = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.China) && t.CityType == CityType.Brown);
        game.TerritoryStates.First(t => t.TerritoryId == chinaCity.Id).HasFactory = true;
        var army = Add(game, Nation.Russia, UnitType.Army, chinaCity.Id, hostile: false);

        var result = ManeuverEngine.ToggleHostility(null, game, army.Id);

        Assert.False(result.Ok);
        Assert.Equal("Cannot blockade the last unoccupied factory of a nation.", result.Error);
        Assert.False(army.IsHostile);
    }

    [Fact]
    public void StandingUpWhereOneForeignNationIsFightsItOnTheSpot()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var neutral = TerritoryData.AllTerritories.First(t => t.Type == TerritoryType.Land && t.Nation == null).Id;
        var army = Add(game, Nation.Russia, UnitType.Army, neutral, hostile: false);
        Add(game, Nation.China, UnitType.Army, neutral);

        Assert.True(ManeuverEngine.ToggleHostility(null, game, army.Id).Ok);
        var battle = ManeuverEngine.ResolveStationaryBattle(null, game, army.Id);

        Assert.True(battle.Ok, battle.Error);
        Assert.True(battle.MoverDestroyed);
        Assert.Empty(game.Units);
    }

    // ---- factory destruction (p.10-11) ---------------------------------------------------------

    [Fact]
    public void ThreeArmiesDestroyAnUndefendedForeignFactoryButNotTheLastOne()
    {
        var (game, gov, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var cities = TerritoryData.AllTerritories.Where(t => t.IsHomeCity(Nation.China)).ToList();
        game.TerritoryStates.First(t => t.TerritoryId == cities[0].Id).HasFactory = true;
        game.TerritoryStates.First(t => t.TerritoryId == cities[1].Id).HasFactory = true;
        for (int i = 0; i < ManeuverRules.DestroyFactoryArmyCost; i++) Add(game, Nation.Russia, UnitType.Army, cities[0].Id, hostile: false);

        Assert.Equal(new[] { cities[0].Id }, ManeuverEngine.FactoryDestructionCandidates(game, Nation.Russia, gov));

        var result = ManeuverEngine.DestroyFactory(null, game, cities[0].Id);

        Assert.True(result.Ok, result.Error);
        Assert.False(game.TerritoryStates.First(t => t.TerritoryId == cities[0].Id).HasFactory);
        Assert.Empty(game.Units);
        var destruction = Assert.Single(game.Actions, a => a.ActionType == "DestroyFactory");
        var destructionMetadata = JsonSerializer.Deserialize<ActionMetadata>(destruction.Metadata!);
        Assert.Equal(Nation.Russia, destruction.Nation);
        Assert.Equal(Nation.China, destructionMetadata?.DefenderNation);

        // cities[1] is now China's last unoccupied factory.
        for (int i = 0; i < ManeuverRules.DestroyFactoryArmyCost; i++) Add(game, Nation.Russia, UnitType.Army, cities[1].Id, hostile: false);
        Assert.Empty(ManeuverEngine.FactoryDestructionCandidates(game, Nation.Russia, gov));
        Assert.Equal("Cannot destroy the last factory of a nation.", ManeuverEngine.DestroyFactory(null, game, cities[1].Id).Error);
    }

    // ---- battle negotiation -------------------------------------------------------------------

    [Fact]
    public void EachDefenderAnswersInTurnAndTheBattleClosesWhenTheLastHas()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var neutral = TerritoryData.AllTerritories.First(t => t.Type == TerritoryType.Land && t.Nation == null).Id;
        var aggressor = Add(game, Nation.Russia, UnitType.Army, neutral, hostile: false);
        Add(game, Nation.China, UnitType.Army, neutral);
        Add(game, Nation.India, UnitType.Army, neutral);
        game.PendingBattleTerritoryId = neutral;
        game.PendingBattleAggressorNation = Nation.Russia;
        game.PendingBattleAggressorUnitId = aggressor.Id;
        game.PendingBattleDefenders = new List<Nation> { Nation.China, Nation.India };

        var first = ManeuverEngine.RespondToBattle(null, game, Nation.China, fight: false);
        Assert.True(first.Ok, first.Error);
        Assert.False(first.BattleClosed);
        Assert.Equal(new[] { Nation.India }, game.PendingBattleDefenders);

        var second = ManeuverEngine.RespondToBattle(null, game, Nation.India, fight: true);
        Assert.True(second.Ok, second.Error);
        Assert.True(second.Fought);
        Assert.True(second.BattleClosed);
        Assert.Null(game.PendingBattleTerritoryId);
        Assert.Single(game.Units); // China's army survives; Russia's and India's fought
        Assert.Equal(Nation.China, game.Units.Single().Nation);

        Assert.Equal("No pending battle.", ManeuverEngine.RespondToBattle(null, game, Nation.China, false).Error);
    }

    [Fact]
    public void AFightEndsTheNegotiationEvenIfTheAggressorHasOtherUnitsThere()
    {
        // The question put to each defender is about the unit that just arrived (p.8: "asked one after
        // another if they want to do battle" with it). Once one of them has fought it, it is gone and
        // there is nothing left to answer - the aggressor's OTHER units in the region are not what was asked about.
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var neutral = TerritoryData.AllTerritories.First(t => t.Type == TerritoryType.Land && t.Nation == null).Id;
        var arriving = Add(game, Nation.Russia, UnitType.Army, neutral, hostile: false);
        var alreadyThere = Add(game, Nation.Russia, UnitType.Army, neutral, hostile: false);
        Add(game, Nation.China, UnitType.Army, neutral);
        Add(game, Nation.India, UnitType.Army, neutral);
        game.PendingBattleTerritoryId = neutral;
        game.PendingBattleAggressorNation = Nation.Russia;
        game.PendingBattleAggressorUnitId = arriving.Id;
        game.PendingBattleDefenders = new List<Nation> { Nation.China, Nation.India };

        var result = ManeuverEngine.RespondToBattle(null, game, Nation.China, fight: true);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.BattleClosed);
        Assert.Null(game.PendingBattleTerritoryId);
        Assert.Empty(game.PendingBattleDefenders);
        Assert.DoesNotContain(game.Units, u => u.Id == arriving.Id);   // the arriving unit fought and died
        Assert.Contains(game.Units, u => u.Id == alreadyThere.Id);      // the other Russian unit was not part of it
        Assert.Single(game.Units, u => u.Nation == Nation.India);        // India was never asked
    }

    // ---- phases and flags (p.10, step 3) --------------------------------------------------------

    [Fact]
    public void FlagsGoToRegionsHeldExclusivelyNeverToHomeProvinces()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        var neutral = TerritoryData.AllTerritories.Where(t => t.Type == TerritoryType.Land && t.Nation == null).Take(2).ToList();
        var chinaHome = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.China)).Id;
        Add(game, Nation.Russia, UnitType.Army, neutral[0].Id);
        Add(game, Nation.Russia, UnitType.Army, neutral[1].Id);
        Add(game, Nation.China, UnitType.Army, neutral[1].Id); // contested
        Add(game, Nation.Russia, UnitType.Army, chinaHome, hostile: true);

        ManeuverEngine.UpdateTerritoryControl(null, game);

        Assert.Equal(Nation.Russia, game.TerritoryStates.First(t => t.TerritoryId == neutral[0].Id).Controller);
        Assert.Null(game.TerritoryStates.First(t => t.TerritoryId == neutral[1].Id).Controller);
        Assert.Null(game.TerritoryStates.First(t => t.TerritoryId == chinaHome).Controller);
        Assert.Single(game.Actions, a => a.ActionType == "FlagPlacement");
    }

    [Fact]
    public void ThePhaseEndsByItselfOnceEveryUnitOfTheKindHasMoved()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var home = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.Russia) && t.CityType == CityType.Brown).Id;
        var army = Add(game, Nation.Russia, UnitType.Army, home);

        ManeuverEngine.TryAutoAdvanceManeuver(null, game, Nation.Russia); // no fleets at all
        Assert.Equal(ManeuverPhase.Armies, game.CurrentManeuverPhase);
        Assert.Single(game.Actions, a => a.ActionType == "AutoEndPhase");

        Assert.True(ManeuverEngine.Stay(null, game, army.Id).Ok);
        ManeuverEngine.TryAutoAdvanceManeuver(null, game, Nation.Russia);
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
        Assert.Equal(2, game.Actions.Count(a => a.ActionType == "AutoEndPhase"));
    }

    [Fact]
    public void EndingThePhaseByHandPlacesFlagsAndMovesOn()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Fleets);
        var neutral = TerritoryData.AllTerritories.First(t => t.Type == TerritoryType.Land && t.Nation == null).Id;
        Add(game, Nation.Russia, UnitType.Army, neutral);

        Assert.True(ManeuverEngine.EndPhase(null, game).Ok);
        Assert.Equal(ManeuverPhase.Armies, game.CurrentManeuverPhase);
        Assert.Equal(Nation.Russia, game.TerritoryStates.First(t => t.TerritoryId == neutral).Controller);
        Assert.Single(game.Actions, a => a.ActionType == "EndPhase");

        Assert.True(ManeuverEngine.EndPhase(null, game).Ok);
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
        Assert.Equal("Invalid phase transition.", ManeuverEngine.EndPhase(null, game).Error);
    }

    // ---- a phase with nothing that can move ends by itself ----------------------------------------

    /// <summary>
    /// An army on an island can only leave by convoy over the nation's own fleets (p.9). With no fleet
    /// to carry it there is no move to make, and the phase should not wait for a "stay" that is the
    /// only answer; with a fleet in the adjacent sea the army can still sail, so the phase stays open.
    /// </summary>
    [Fact]
    public void TheArmiesPhaseEndsWhenNoArmyHasAnywhereToGo()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        Add(game, Nation.Russia, UnitType.Army, "Japan");

        ManeuverEngine.TryAutoAdvanceManeuver(null, game, Nation.Russia);

        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
        Assert.Contains(game.Actions, a => a.ActionType == "AutoEndPhase");
    }

    [Fact]
    public void TheArmiesPhaseStaysOpenWhileAnArmyCanStillBeConvoyed()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        Add(game, Nation.Russia, UnitType.Army, "Japan");
        Add(game, Nation.Russia, UnitType.Fleet, "SeaOfJapan");

        ManeuverEngine.TryAutoAdvanceManeuver(null, game, Nation.Russia);

        Assert.Equal(ManeuverPhase.Armies, game.CurrentManeuverPhase);
    }

    [Fact]
    public void TheArmiesPhaseStaysOpenWhileAnyArmyCanMove()
    {
        var (game, _, _) = BuildGame(Nation.Russia, ManeuverPhase.Armies);
        Add(game, Nation.Russia, UnitType.Army, "Japan");
        Add(game, Nation.Russia, UnitType.Army, "Moscow");

        ManeuverEngine.TryAutoAdvanceManeuver(null, game, Nation.Russia);

        Assert.Equal(ManeuverPhase.Armies, game.CurrentManeuverPhase);
    }
}
