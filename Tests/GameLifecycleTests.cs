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
/// Set-up, pinned directly against the engine. Imperial-2030-Rules.pdf p.4-5: starting money by player
/// count (13M / 24M / 35M), the nation flag cards dealt with the bonds printed on their backs (the 9M of the
/// nation and the 2M of the next), "each player will have 2 million remaining", an undealt card going to
/// the holder of that nation's 2M bond, and the investor card to the left of Russia's government.
/// </summary>
public class GameLifecycleTests
{
    private static Game BuildLobby(int playerCount)
    {
        var game = new Game { Id = Guid.NewGuid(), Name = "Setup", Status = GameStatus.Lobby, CurrentTurnNation = Nation.Russia, MaxPlayers = 6 };
        for (int i = 0; i < playerCount; i++)
        {
            game.Players.Add(new Player { Id = Guid.NewGuid(), GameId = game.Id, IsBot = true, BotType = "Default", BotName = $"P{i}", IsHost = i == 0 });
        }
        return game;
    }

    [Fact]
    public void SixPlayersEachGovernOneNationWithTwoMillionLeft()
    {
        var game = BuildLobby(6);

        var result = GameLifecycle.Start(null, game, forcedDistribution: null, startedBy: "host");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(GameStatus.InProgress, game.Status);
        Assert.NotNull(game.StartedAt);
        Assert.Equal(6, game.NationStates.Count);
        Assert.Equal(54, game.Bonds.Count);
        Assert.Equal(TerritoryData.AllTerritories.Count, game.TerritoryStates.Count);
        Assert.Equal(12, game.TerritoryStates.Count(t => t.HasFactory));
        Assert.All(game.NationStates, ns => Assert.Equal(11, ns.Treasury)); // 9M + 2M paid in
        Assert.All(game.NationStates, ns => Assert.NotNull(ns.ControllerId));
        Assert.Equal(6, game.NationStates.Select(ns => ns.ControllerId).Distinct().Count());
        Assert.All(game.Players, p => Assert.Equal(2, p.Cash));
        Assert.Equal(6, result.Distribution.Count);

        // The government holds the 9M of its nation and the 2M of the next one round.
        foreach (var ns in game.NationStates)
        {
            var nine = game.Bonds.First(b => b.Nation == ns.Nation && b.Cost == 9);
            Assert.Equal(ns.ControllerId, nine.HolderId);
        }

        // Investor card: left of Russia's government in seating order (p.5).
        var seating = game.Players.OrderBy(p => p.Id).ToList();
        var russiaGov = seating.FindIndex(p => p.Id == game.NationStates.First(ns => ns.Nation == Nation.Russia).ControllerId);
        Assert.Equal(seating[(russiaGov + 1) % seating.Count].Id, game.InvestorCardHolderId);

        Assert.Equal(Nation.Russia, game.CurrentTurnNation);
        var start = Assert.Single(game.Actions, a => a.ActionType == "StartGame");
        Assert.Equal("host", start.PlayerName);
        Assert.Contains("\"NationDistribution\"", start.Metadata);
    }

    [Fact]
    public void AForcedDistributionIsReproducedExactly()
    {
        var game = BuildLobby(4);
        var seating = game.Players.OrderBy(p => p.Id).ToList();
        var forced = new Dictionary<Nation, Guid>
        {
            [Nation.Russia] = seating[0].Id, [Nation.India] = seating[1].Id, [Nation.USA] = seating[2].Id, [Nation.Europe] = seating[3].Id
        };

        var result = GameLifecycle.Start(null, game, forced, "host");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(forced, result.Distribution);
        foreach (var kvp in forced)
        {
            Assert.Equal(kvp.Value, game.NationStates.First(ns => ns.Nation == kvp.Key).ControllerId);
        }
        // China and Brazil were not dealt: their flag cards go to whoever holds their 2M bond -
        // Russia's package carries China's 2M, India's carries Brazil's 2M (p.5).
        Assert.Equal(seating[0].Id, game.NationStates.First(ns => ns.Nation == Nation.China).ControllerId);
        Assert.Equal(seating[1].Id, game.NationStates.First(ns => ns.Nation == Nation.Brazil).ControllerId);
        Assert.All(game.Players, p => Assert.Equal(2, p.Cash));
    }

    [Theory]
    [InlineData(2, 35, 3)]
    [InlineData(3, 24, 2)]
    public void FewerPlayersStartWithMoreMoneyAndMoreNations(int players, int startingCash, int packagesEach)
    {
        var game = BuildLobby(players);

        var result = GameLifecycle.Start(null, game, null, "host");

        Assert.True(result.Ok, result.Error);
        Assert.All(game.Players, p => Assert.Equal(startingCash - packagesEach * 11, p.Cash));
        Assert.All(game.NationStates, ns => Assert.NotNull(ns.ControllerId));
        Assert.Equal(packagesEach * players, result.Distribution.Count);
    }

    [Fact]
    public void TheFirstTurnGoesToTheFirstGovernedNation()
    {
        var game = BuildLobby(4);
        var seating = game.Players.OrderBy(p => p.Id).ToList();
        // Nobody holds anything of Russia: its card stays in the bank, so China opens.
        var forced = new Dictionary<Nation, Guid>
        {
            [Nation.China] = seating[0].Id, [Nation.India] = seating[1].Id, [Nation.Brazil] = seating[2].Id, [Nation.USA] = seating[3].Id
        };

        var result = GameLifecycle.Start(null, game, forced, "host");

        Assert.True(result.Ok, result.Error);
        Assert.Null(game.NationStates.First(ns => ns.Nation == Nation.Russia).ControllerId);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
        // No Russian government: the investor card goes to the left of China's (p.5).
        Assert.Equal(seating[1].Id, game.InvestorCardHolderId);
    }

    [Fact]
    public void RefusesWithTooFewPlayersOrWhenAlreadySetUp()
    {
        Assert.False(GameLifecycle.Start(null, BuildLobby(1), null, "host").Ok);

        var game = BuildLobby(6);
        Assert.True(GameLifecycle.Start(null, game, null, "host").Ok);
        var again = GameLifecycle.Start(null, game, null, "host");
        Assert.False(again.Ok);
        Assert.Equal(54, game.Bonds.Count);
    }
}
