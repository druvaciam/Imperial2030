using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// A bot must not blockade a nation's last unoccupied factory by standing an army it already has there
/// upright, rather than by walking into the province.
///
/// Imperial-2030-Rules.pdf p.10: "If a nation has only one factory left that is not occupied by hostile
/// armies (standing upright), the province of this factory may not be entered by hostile armies. Armies
/// of other nations that enter this province are laid down on their sides."
///
/// Observed live: "Bot Charlie (RL-4) / Brazil / army in Mumbai converted to hostile", which blockaded
/// India's last factory. Mumbai is India's light-blue home city, so the blockade also cut off production,
/// import, factory building, taxation and rail there.
///
/// ManeuverController.ToggleHostility already refuses exactly this, and its comment says why: the entry
/// rule "could simply be walked around by entering peacefully and standing the army upright afterwards."
/// BotService has its own in-place conversion path for an army that stays put, and that path did not
/// carry the guard - so the protection held for humans and not for bots.
/// </summary>
public class BotLastFactoryBlockadeTests
{
    /// <summary>
    /// Keeps every army where it is and always chooses hostility, so the test lands deterministically on
    /// the stay-put conversion branch instead of depending on how a real strategy happens to score
    /// neighbours on this board.
    /// </summary>
    private sealed class StayPutHostileStrategy : BotStrategyBase
    {
        public const string BotType = "TestStayPutHostile";

        public override string Name => BotType;

        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units) => 0;

        public override bool RetreatFromBattle(Game game, PendingBattle battle) => false;

        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller) =>
            destinationId == unit.TerritoryId ? 100.0 : -100.0;

        public override bool DetermineHostility(bool hasEnemy, bool isForeignHome) => true;
    }

    private static ApplicationDbContext GetDbContext(string dbName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options);

    private static BotService BuildBotService(string dbName)
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(s => s.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(GetDbContext(dbName));
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
            return scope.Object;
        });

        return new BotService(
            scopeFactory.Object,
            hub.Object,
            new List<IBotStrategy> { new StayPutHostileStrategy(), new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance)
        { SkipDelays = true };
    }

    /// <summary>
    /// Mumbai holds India's only factory, and a Brazilian army is already standing there peacefully -
    /// which is the state p.10 forces on an army that enters. Brazil then maneuvers.
    /// </summary>
    private static (Game Game, Unit Invader, Player BotPlayer, NationState BrazilState) BuildBoard()
    {
        var botPlayer = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = StayPutHostileStrategy.BotType, BotName = "Bot Charlie" };
        var humanPlayer = new Player { Id = Guid.NewGuid(), UserId = "human" };

        var brazil = new NationState { Nation = Nation.Brazil, ControllerId = botPlayer.Id };
        var india = new NationState { Nation = Nation.India, ControllerId = humanPlayer.Id };

        var invader = new Unit
        {
            Id = Guid.NewGuid(),
            Nation = Nation.Brazil,
            UnitType = UnitType.Army,
            TerritoryId = "Mumbai",
            IsHostile = false // arrived peacefully, as the rule requires
        };

        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Last factory blockade",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Brazil,
            CurrentManeuverPhase = ManeuverPhase.Armies,
            Players = new List<Player> { botPlayer, humanPlayer },
            NationStates = new List<NationState> { brazil, india },
            // Mumbai is India's ONLY factory, so it is the one p.10 protects.
            TerritoryStates = new List<TerritoryState> { new() { TerritoryId = "Mumbai", HasFactory = true } },
            Units = new List<Unit>
            {
                invader,
                // India's fleet, in its own harbour.
                new() { Id = Guid.NewGuid(), Nation = Nation.India, UnitType = UnitType.Fleet, TerritoryId = "Mumbai", IsHostile = false },
            },
            Actions = new List<GameAction>()
        };

        return (game, invader, botPlayer, brazil);
    }

    [Fact]
    public async Task ABotCannotStandAnArmyUprightInANationsLastUnoccupiedFactory()
    {
        var (game, invader, botPlayer, brazil) = BuildBoard();
        var botService = BuildBotService(Guid.NewGuid().ToString());

        await botService.BotManeuver(null, game, brazil, botPlayer);

        Assert.False(invader.IsHostile,
            "The army was standing in India's only factory province. p.10 forbids a hostile army there, " +
            "and ManeuverController.ToggleHostility already refuses the same conversion for a human.");
    }

    /// <summary>
    /// The guard must be about the LAST factory, not about foreign home provinces generally - a bot that
    /// could never turn hostile anywhere would be a far worse regression than the bug being fixed.
    /// </summary>
    [Fact]
    public async Task ABotCanStillStandAnArmyUprightWhenTheNationHasAnotherFreeFactory()
    {
        var (game, invader, botPlayer, brazil) = BuildBoard();

        // Kolkata is a second India factory and nothing is blockading it, so Mumbai is no longer the last one.
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Kolkata", HasFactory = true });

        var botService = BuildBotService(Guid.NewGuid().ToString());

        await botService.BotManeuver(null, game, brazil, botPlayer);

        Assert.True(invader.IsHostile,
            "With two unblockaded factories, blockading one of them is legal and the bot must still be able to.");
    }

    /// <summary>
    /// Standing an army upright in a foreign home province is the same act of aggression as walking in
    /// hostilely, and p.10 lets the defender answer it: "Armies of foreign nations can call for a battle
    /// if their land region has been invaded", and "Fleets and armies can battle against each other only
    /// if the fleet is still in the harbor. In this case, an invading army can attack the fleet or the
    /// fleet can call for a battle."
    ///
    /// Mumbai is India's light-blue city, so a fleet sitting there is in its harbour. The bot's hostile
    /// MOVE path already resolves this correctly; its stay-put conversion path silently occupied the
    /// province with the defender still in it. Humans have always been able to fight here through the
    /// stationary Battle endpoint - only bots could not.
    /// </summary>
    [Fact]
    public async Task TurningHostileInPlaceFightsAHarbouredDefendingFleet()
    {
        var (game, invader, botPlayer, brazil) = BuildBoard();

        // A second free factory, so the conversion itself is legal and the test isolates the battle.
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Kolkata", HasFactory = true });

        var defendingFleet = game.Units.First(u => u.Nation == Nation.India);
        var botService = BuildBotService(Guid.NewGuid().ToString());

        await botService.BotManeuver(null, game, brazil, botPlayer);

        Assert.DoesNotContain(defendingFleet, game.Units);
        Assert.DoesNotContain(invader, game.Units);
        Assert.Contains(game.Actions, a => a.ActionType == "Battle");
    }

    /// <summary>
    /// The mirror case: no defender, so turning hostile is an ordinary blockade and nothing should die.
    /// Without this, a battle helper that fired on an empty province would pass the test above while
    /// destroying units for no reason.
    /// </summary>
    [Fact]
    public async Task TurningHostileInPlaceWithNoDefenderDestroysNothing()
    {
        var (game, invader, botPlayer, brazil) = BuildBoard();
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Kolkata", HasFactory = true });
        foreach (var indiaUnit in game.Units.Where(u => u.Nation == Nation.India).ToList())
        {
            game.Units.Remove(indiaUnit);
        }

        var botService = BuildBotService(Guid.NewGuid().ToString());

        await botService.BotManeuver(null, game, brazil, botPlayer);

        Assert.Contains(invader, game.Units);
        Assert.True(invader.IsHostile);
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "Battle");
    }
}
