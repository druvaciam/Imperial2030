using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// A bot that lands on Factory and builds nothing must say so.
///
/// Observed live:
///   [04:34:34] Bot Bravo (RL-4) India  moved to Factory from Production (Cost: 0M)
///   [04:34:44] Bot Bravo (RL-4) India  ended their turn
///
/// Ten seconds, no explanation. BotBuildFactory returned silently when the treasury could not cover the
/// 5M (Imperial-2030-Rules.pdf p.7), so the turn read as a stall rather than as a decision - and the
/// pause was doubled because ExecuteBotTurn waits once after the rondel move and again before ending the
/// turn, with nothing rendered in between.
/// </summary>
public class BotFactorySkipLoggingTests
{
    private static (Game Game, NationState India, Player Bot) BuildBoard(
        int treasury, bool everyCityBuilt, bool blockadeEveryFreeCity = false)
    {
        var bot = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Bot Bravo" };
        var india = new NationState
        {
            Nation = Nation.India,
            ControllerId = bot.Id,
            Treasury = treasury,
            RondelPosition = RondelData.FactorySlot
        };

        var homeCities = TerritoryData.AllTerritories
            .Where(t => t.Nation == Nation.India && t.CityType != CityType.None)
            .ToList();

        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Factory skip logging",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.India,
            Players = new List<Player> { bot },
            NationStates = new List<NationState> { india },
            TerritoryStates = homeCities
                .Select(c => new TerritoryState { TerritoryId = c.Id, HasFactory = everyCityBuilt })
                .ToList(),
            Units = new List<Unit>(),
            Actions = new List<GameAction>()
        };

        if (blockadeEveryFreeCity)
        {
            foreach (var city in homeCities)
            {
                // p.7: a hostile army standing in a home province stops a factory being built there.
                game.Units.Add(new Unit
                {
                    Id = Guid.NewGuid(),
                    Nation = Nation.Brazil,
                    UnitType = UnitType.Army,
                    TerritoryId = city.Id,
                    IsHostile = true
                });
            }
        }

        return (game, india, bot);
    }

    private static BotService BuildBotService() =>
        new(new Moq.Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().Object,
            BuildHub(),
            new List<IBotStrategy> { new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance)
        { SkipDelays = true };

    private static Microsoft.AspNetCore.SignalR.IHubContext<Imperial2030.Server.Hubs.GameHub> BuildHub()
    {
        var hub = new Moq.Mock<Microsoft.AspNetCore.SignalR.IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Moq.Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(Moq.It.IsAny<string>()))
               .Returns(new Moq.Mock<Microsoft.AspNetCore.SignalR.IClientProxy>().Object);
        return hub.Object;
    }

    [Fact]
    public async Task ATreasuryTooSmallForAFactoryIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: GameConstants.FactoryCost - 1, everyCityBuilt: false);

        await BuildBotService().BotBuildFactory(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "FactoryNoFunds");
        Assert.True(india.HasBuiltThisTurn, "The decision is resolved either way, or the turn hangs.");
    }

    /// <summary>The four-city ceiling: one factory per home city (p.7), all of them already built.</summary>
    [Fact]
    public async Task ReachingTheFactoryLimitIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: true);

        await BuildBotService().BotBuildFactory(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "FactoryAllBuilt");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "FactoryBlockaded");
    }

    /// <summary>
    /// Cities are free but every one of them has a hostile army standing in it, so p.7 forbids building.
    /// Distinguished from the limit case because the two call for completely different responses - one is
    /// permanent, the other is someone to fight.
    /// </summary>
    [Fact]
    public async Task OccupationOfEveryFreeCityIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: false, blockadeEveryFreeCity: true);

        await BuildBotService().BotBuildFactory(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "FactoryBlockaded");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "FactoryAllBuilt");
    }

    /// <summary>
    /// The mirror case: a build that succeeds must not also claim it was skipped.
    /// </summary>
    [Fact]
    public async Task ASuccessfulBuildLogsOnlyTheBuild()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: false);

        await BuildBotService().BotBuildFactory(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "Factory");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "FactoryNoFunds" || a.ActionType == "FactoryNoSite");
        Assert.Equal(20 - GameConstants.FactoryCost, india.Treasury);
    }

    /// <summary>Declines every city, so a build was possible and simply not taken.</summary>
    private sealed class NeverBuildsStrategy : BotStrategyBase
    {
        public const string BotType = "TestNeverBuilds";

        public override string Name => BotType;
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units) => 0;
        public override bool RetreatFromBattle(Game game, PendingBattle battle) => false;
        public override string? ChooseCityForFactory(Game game, Nation nation, List<Territory> validCities) => null;
    }

    /// <summary>
    /// The last way a Factory turn could still go silent: money in hand, a free city available, and the
    /// strategy declines. It is a decision rather than a constraint, so it gets its own entry - this is
    /// the case TcpTrainingServer penalises as an avoidable factory skip.
    /// </summary>
    [Fact]
    public async Task ChoosingNotToBuildIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: false);
        bot.BotType = NeverBuildsStrategy.BotType;

        var botService = new BotService(
            new Moq.Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().Object,
            BuildHub(),
            new List<IBotStrategy> { new NeverBuildsStrategy(), new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance)
        { SkipDelays = true };

        await botService.BotBuildFactory(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "FactoryDeclined");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "Factory");
        Assert.Equal(20, india.Treasury);
    }

    // ---- the other rondel slots that could produce a turn with no visible effect ----------------

    [Fact]
    public async Task ATreasuryTooSmallToImportIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: GameConstants.ImportUnitCost - 1, everyCityBuilt: false);

        await BuildBotService().BotImport(null, game, india);

        Assert.Contains(game.Actions, a => a.ActionType == "ImportNoFunds");
        Assert.True(india.HasImportedThisTurn);
    }

    /// <summary>
    /// A nation with no factory produces nothing, and must say so rather than logging
    /// "produced 0 units ()" - which reads as a rendering fault rather than a game state.
    /// </summary>
    [Fact]
    public async Task ProducingWithNoFactoriesIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: false);

        await BuildBotService().BotProduction(null, game, india);

        Assert.Contains(game.Actions, a => a.ActionType == "ProductionNoFactories");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "Production");
    }

    /// <summary>
    /// p.7: "Factories in a home province in which hostile armies (standing upright) are present cannot
    /// produce." Distinguished from the unit-cap case because one is an enemy to remove and the other is
    /// a ceiling to live with.
    /// </summary>
    [Fact]
    public async Task ProducingWithEveryFactoryBlockadedIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: true, blockadeEveryFreeCity: true);

        await BuildBotService().BotProduction(null, game, india);

        Assert.Contains(game.Actions, a => a.ActionType == "ProductionBlockaded");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "ProductionAtUnitCap");
    }

    /// <summary>A working factory that produces anyway must still log the normal Production entry.</summary>
    [Fact]
    public async Task AProductiveTurnLogsTheNormalEntry()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: true);

        await BuildBotService().BotProduction(null, game, india);

        Assert.Contains(game.Actions, a => a.ActionType == "Production");
        Assert.DoesNotContain(game.Actions, a => a.ActionType.StartsWith("Production", StringComparison.Ordinal)
                                                 && a.ActionType != "Production");
    }

    /// <summary>
    /// The normal reason a Production turn produces nothing. The engine forbids hostile entry into a
    /// nation's last unoccupied factory, so at least one factory is always available - which means a
    /// fully wasted Production turn always involves the unit cap, not blockade alone.
    /// </summary>
    [Fact]
    public async Task ProducingAtTheUnitCapIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: true);

        // Fill India to both caps, so every working factory has nothing left to make.
        foreach (var (type, cap) in new[]
                 {
                     (UnitType.Army, NationData.GetMaxArmies(Nation.India)),
                     (UnitType.Fleet, NationData.GetMaxFleets(Nation.India)),
                 })
        {
            for (int i = 0; i < cap; i++)
            {
                game.Units.Add(new Unit
                {
                    Id = Guid.NewGuid(),
                    Nation = Nation.India,
                    UnitType = type,
                    TerritoryId = "NewDelhi",
                    IsHostile = false
                });
            }
        }

        await BuildBotService().BotProduction(null, game, india);

        Assert.Contains(game.Actions, a => a.ActionType == "ProductionAtUnitCap");
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "ProductionBlockaded");
    }

    [Fact]
    public async Task ManeuveringWithNoUnitsIsLogged()
    {
        var (game, india, bot) = BuildBoard(treasury: 20, everyCityBuilt: false);
        game.CurrentManeuverPhase = ManeuverPhase.Fleets;

        await BuildBotService().BotManeuver(null, game, india, bot);

        Assert.Contains(game.Actions, a => a.ActionType == "ManeuverNoUnits");
    }
}
