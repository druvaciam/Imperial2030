using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The four single-step rondel actions and the turn end, each pinned directly against its engine. The
/// callers - endpoint, bot, training - are covered by their own suites; these fix what the operation
/// itself does and refuses.
/// </summary>
public class RondelActionEngineTests
{
    private static (Game Game, NationState Ns, Player Gov) BuildGame(Nation nation, int rondelSlot, int treasury = 0)
    {
        var gov = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Gov", Cash = 5 };
        var ns = new NationState { Nation = nation, ControllerId = gov.Id, RondelPosition = rondelSlot, Treasury = treasury, Power = 0 };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Rondel action",
            Status = GameStatus.InProgress,
            CurrentTurnNation = nation,
            Players = new List<Player> { gov },
            NationStates = Enum.GetValues<Nation>()
                .Select(n => n == nation ? ns : new NationState { Nation = n, ControllerId = gov.Id, RondelPosition = 0 })
                .ToList(),
            Units = new List<Unit>(),
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };
        return (game, ns, gov);
    }

    private static Territory HomeCity(Nation nation, CityType type) =>
        TerritoryData.AllTerritories.First(t => t.IsHomeCity(nation) && t.CityType == type);

    // ---- Factory (p.7) -------------------------------------------------------------------------

    [Fact]
    public void FactoryCostsFiveFromTheTreasuryAndIsOncePerTurn()
    {
        var (game, ns, _) = BuildGame(Nation.Russia, RondelData.FactorySlot, treasury: 7);
        var city = HomeCity(Nation.Russia, CityType.Brown);

        var result = FactoryEngine.BuildFactory(null, game, city.Id);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(7 - GameConstants.FactoryCost, ns.Treasury);
        Assert.True(game.TerritoryStates.First(t => t.TerritoryId == city.Id).HasFactory);
        Assert.True(ns.HasBuiltThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "Factory");

        var second = FactoryEngine.BuildFactory(null, game, HomeCity(Nation.Russia, CityType.LightBlue).Id);
        Assert.False(second.Ok);
        Assert.Equal("Already built factory this turn.", second.Error);
    }

    [Fact]
    public void FactoryRefusesAForeignCityAnOccupiedCityAndAnEmptyTreasury()
    {
        var (game, ns, _) = BuildGame(Nation.Russia, RondelData.FactorySlot, treasury: 5);

        Assert.Equal("Can only build in Russia's home cities.",
            FactoryEngine.BuildFactory(null, game, HomeCity(Nation.China, CityType.Brown).Id).Error);

        var city = HomeCity(Nation.Russia, CityType.Brown);
        game.Units.Add(new Unit { Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = city.Id, IsHostile = true });
        Assert.Equal("Cannot build factory: hostile foreign armies are present in the city.",
            FactoryEngine.BuildFactory(null, game, city.Id).Error);
        Assert.False(FactoryEngine.IsBuildableSite(game, Nation.Russia, city.Id));

        game.Units.Clear();
        ns.Treasury = GameConstants.FactoryCost - 1;
        Assert.Equal($"Nation treasury insufficient. Need {GameConstants.FactoryCost}M.",
            FactoryEngine.BuildFactory(null, game, city.Id).Error);
        Assert.False(ns.HasBuiltThisTurn);
    }

    [Fact]
    public void BuildableCitiesAreTheHomeCitiesWithoutAFactoryOrAHostileArmy()
    {
        var (game, _, _) = BuildGame(Nation.India, RondelData.FactorySlot);
        var cities = TerritoryData.AllTerritories.Where(t => t.IsHomeCity(Nation.India)).ToList();
        game.TerritoryStates.First(t => t.TerritoryId == cities[0].Id).HasFactory = true;
        game.Units.Add(new Unit { Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = cities[1].Id, IsHostile = true });

        var buildable = FactoryEngine.BuildableCities(game, Nation.India).Select(t => t.Id).ToList();

        Assert.Equal(cities.Skip(2).Select(t => t.Id), buildable);
    }

    // ---- Taxation (p.9, p.12) -----------------------------------------------------------------

    [Fact]
    public void TaxationLogsAdvancesTheTurnAndRefusesOffTheSlot()
    {
        var (game, ns, _) = BuildGame(Nation.Brazil, RondelData.TaxationSlot);
        game.TerritoryStates.First(t => t.TerritoryId == HomeCity(Nation.Brazil, CityType.Brown).Id).HasFactory = true;

        var result = TaxationEngine.ExecuteTaxation(null, game);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.GameEnded);
        Assert.Single(game.Actions, a => a.ActionType == "Taxation");
        Assert.NotEqual(Nation.Brazil, game.CurrentTurnNation); // taxation ends the turn by itself
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "EndTurn");

        var (game2, _, _) = BuildGame(Nation.Brazil, RondelData.ProductionSlot1);
        Assert.Equal("Nation must be on 'Taxation' slot.", TaxationEngine.ExecuteTaxation(null, game2).Error);
    }

    [Fact]
    public void ReachingMaxPowerOnTaxationEndsTheGameInsteadOfAdvancing()
    {
        var (game, ns, _) = BuildGame(Nation.Brazil, RondelData.TaxationSlot);
        ns.Power = GameConstants.MaxPowerPoints - 1;
        // Enough revenue for at least one power point: a factory and some flags.
        game.TerritoryStates.First(t => t.TerritoryId == HomeCity(Nation.Brazil, CityType.Brown).Id).HasFactory = true;
        game.TerritoryStates.First(t => t.TerritoryId == HomeCity(Nation.Brazil, CityType.LightBlue).Id).HasFactory = true;
        foreach (var flag in TerritoryData.AllTerritories.Where(t => t.Nation == null && t.Type != TerritoryType.Sea).Take(4))
        {
            game.TerritoryStates.First(t => t.TerritoryId == flag.Id).Controller = Nation.Brazil;
        }

        var result = TaxationEngine.ExecuteTaxation(null, game);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.GameEnded, $"power after taxation: {ns.Power}");
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.NotNull(game.FinishedAt);
        Assert.Equal(Nation.Brazil, game.CurrentTurnNation); // no advance once the game is over
    }

    // ---- Production (p.7) ----------------------------------------------------------------------

    [Fact]
    public void ProductionMakesOneUnitPerFactoryOfTheRightTypeAndIsOncePerTurn()
    {
        var (game, ns, _) = BuildGame(Nation.USA, RondelData.ProductionSlot1);
        var brown = HomeCity(Nation.USA, CityType.Brown);
        var port = HomeCity(Nation.USA, CityType.LightBlue);
        game.TerritoryStates.First(t => t.TerritoryId == brown.Id).HasFactory = true;
        game.TerritoryStates.First(t => t.TerritoryId == port.Id).HasFactory = true;

        var result = ProductionEngine.ExecuteProduction(null, game);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.ProducedCount);
        Assert.Single(game.Units, u => u.TerritoryId == brown.Id && u.UnitType == UnitType.Army);
        Assert.Single(game.Units, u => u.TerritoryId == port.Id && u.UnitType == UnitType.Fleet);
        Assert.True(ns.HasProducedThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "Production");

        Assert.Equal("Already produced this turn.", ProductionEngine.ExecuteProduction(null, game).Error);
    }

    [Fact]
    public void AnEmptyProductionStillCountsAsTheTurnsActionAndSaysWhy()
    {
        // The action was taken whether or not it yielded a unit, so the once-per-turn guard applies and
        // the log says why nothing came of it.
        var (game, ns, _) = BuildGame(Nation.USA, RondelData.ProductionSlot1);

        var result = ProductionEngine.ExecuteProduction(null, game);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, result.ProducedCount);
        Assert.True(ns.HasProducedThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "ProductionNoFactories");

        var occupied = BuildGame(Nation.USA, RondelData.ProductionSlot2);
        var city = HomeCity(Nation.USA, CityType.Brown);
        occupied.Game.TerritoryStates.First(t => t.TerritoryId == city.Id).HasFactory = true;
        occupied.Game.Units.Add(new Unit { Nation = Nation.Brazil, UnitType = UnitType.Army, TerritoryId = city.Id, IsHostile = true });
        Assert.Equal(0, ProductionEngine.ExecuteProduction(null, occupied.Game).ProducedCount);
        Assert.Single(occupied.Game.Actions, a => a.ActionType == "ProductionBlockaded");
    }

    // ---- Import (p.8) --------------------------------------------------------------------------

    [Fact]
    public void ImportBuysUpToThreeUnitsAtOneMillionEachInHomeProvinces()
    {
        var (game, ns, _) = BuildGame(Nation.Europe, RondelData.ImportSlot, treasury: 4);
        var brown = HomeCity(Nation.Europe, CityType.Brown);
        var port = HomeCity(Nation.Europe, CityType.LightBlue);

        var result = ImportEngine.Import(null, game, new[] { (UnitType.Army, brown.Id), (UnitType.Fleet, port.Id), (UnitType.Army, port.Id) });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(4 - 3 * GameConstants.ImportUnitCost, ns.Treasury);
        Assert.Equal(3, game.Units.Count(u => u.Nation == Nation.Europe));
        Assert.True(ns.HasImportedThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "Import");
    }

    [Fact]
    public void ImportRejectsTheWholeRequestWhenAnyUnitIsIllegalAndPlacesNothing()
    {
        var (game, ns, _) = BuildGame(Nation.Europe, RondelData.ImportSlot, treasury: 4);
        var brown = HomeCity(Nation.Europe, CityType.Brown);

        // A fleet in a city with no harbor is the illegal third unit.
        var result = ImportEngine.Import(null, game, new[] { (UnitType.Army, brown.Id), (UnitType.Army, brown.Id), (UnitType.Fleet, brown.Id) });

        Assert.False(result.Ok);
        Assert.Contains("no harbor", result.Error);
        Assert.Empty(game.Units);
        Assert.Equal(4, ns.Treasury);
        Assert.False(ns.HasImportedThisTurn);

        Assert.Equal($"Cannot import more than {GameConstants.MaxImportUnits} units.",
            ImportEngine.Import(null, game, Enumerable.Repeat((UnitType.Army, brown.Id), 4).ToList()).Error);
        ns.Treasury = 2;
        Assert.Equal("Insufficient treasury. Cost: 3M",
            ImportEngine.Import(null, game, new[] { (UnitType.Army, brown.Id), (UnitType.Army, brown.Id), (UnitType.Army, brown.Id) }).Error);
        Assert.Empty(game.Units);
    }

    [Fact]
    public void ImportCountsTheRequestItselfAgainstTheUnitCap()
    {
        var (game, _, _) = BuildGame(Nation.Europe, RondelData.ImportSlot, treasury: 10);
        var brown = HomeCity(Nation.Europe, CityType.Brown);
        int max = NationData.GetMaxArmies(Nation.Europe);
        for (int i = 0; i < max - 1; i++)
        {
            game.Units.Add(new Unit { Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = brown.Id });
        }

        // One more fits; two do not, and that must be caught before anything is placed.
        var result = ImportEngine.Import(null, game, new[] { (UnitType.Army, brown.Id), (UnitType.Army, brown.Id) });

        Assert.False(result.Ok);
        Assert.Contains("maximum allowed", result.Error);
        Assert.Equal(max - 1, game.Units.Count);
    }

    [Fact]
    public void StepwiseImportPlacesOneUnitAtATimeAndLogsOnceWhenCompleted()
    {
        // The training server's shape: PlaceOne per agent step, then CompleteImport.
        var (game, ns, _) = BuildGame(Nation.China, RondelData.ImportSlot, treasury: 2);
        var brown = HomeCity(Nation.China, CityType.Brown);

        Assert.True(ImportEngine.PlaceOne(null, game, brown.Id, UnitType.Army).Ok);
        Assert.Equal(1, ns.Treasury);
        Assert.False(ns.HasImportedThisTurn);
        Assert.Empty(game.Actions);

        Assert.True(ImportEngine.PlaceOne(null, game, brown.Id, UnitType.Army).Ok);
        Assert.Equal("Insufficient treasury. Cost: 1M", ImportEngine.PlaceOne(null, game, brown.Id, UnitType.Army).Error);

        ImportEngine.CompleteImport(null, game, new[] { (UnitType.Army, brown.Id), (UnitType.Army, brown.Id) });

        Assert.True(ns.HasImportedThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "Import");
        Assert.Equal("Already imported this turn.", ImportEngine.PlaceOne(null, game, brown.Id, UnitType.Army).Error);
    }

    // ---- End turn (p.7) ------------------------------------------------------------------------

    [Fact]
    public void EndTurnAdvancesTheRotationAndLogsForTheNationThatFinished()
    {
        var (game, _, _) = BuildGame(Nation.Russia, RondelData.ProductionSlot1);

        var result = TurnEngine.EndTurn(null, game);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
        var entry = Assert.Single(game.Actions, a => a.ActionType == "EndTurn");
        Assert.Equal(Nation.Russia, entry.Nation);
    }

    [Fact]
    public void EndTurnRefusesWhileAnythingIsStillOpen()
    {
        var (game, _, _) = BuildGame(Nation.Russia, RondelData.ManeuverSlot1);

        game.CurrentManeuverPhase = ManeuverPhase.Armies;
        Assert.Equal("Finish your maneuver phase (Armies) first.", TurnEngine.EndTurn(null, game).Error);

        game.CurrentManeuverPhase = ManeuverPhase.None;
        game.PendingBattleDefenders = new List<Nation> { Nation.China };
        Assert.Equal("Cannot end turn while a battle is pending.", TurnEngine.EndTurn(null, game).Error);

        game.PendingBattleDefenders = new List<Nation>();
        game.IsInvestorTurn = true;
        Assert.Equal("Waiting for Investor Phase.", TurnEngine.EndTurn(null, game).Error);

        Assert.Equal(Nation.Russia, game.CurrentTurnNation);
        Assert.Empty(game.Actions);
    }
}
