using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Every nation with a government keeps getting its turn, in order.
///
/// Imperial-2030-Rules.pdf p.7: "Russia begins the game; after that, the nations move clockwise according
/// to the order of their national treasuries on the edge of the game board (1. Russia, 2. China, 3.
/// India, 4. Brazil, 5. United States and 6. Europe)." The ONLY sanctioned skip is p.4's "In case a
/// nation has not been granted a bond, it has no government yet. In this case, the turn of this nation is
/// skipped" - and bonds are never returned mid-game.
///
/// These pin <see cref="Game.AdvanceTurn"/> against those two sentences and nothing more. They are NOT a
/// guard against the four-nations-stop-acting defect that prompted them: the rotation was never at fault
/// there. In the exported training session the cycle was intact throughout - China -> India -> Brazil ->
/// USA -> Europe -> Russia, then the same cycle minus China, then minus Brazil - so those nations were
/// still receiving turns; the turns produced nothing, because each was consumed by a Factory build/skip
/// decision the agent had already made. See Tests/TrainingFactorySlotTrapTests.cs.
/// </summary>
public class TurnRotationTests
{
    private static Game BuildGame(Func<Nation, Guid?> controllerFor) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Rotation",
        Status = GameStatus.InProgress,
        CurrentTurnNation = Nation.Russia,
        NationStates = Enum.GetValues<Nation>()
            .Select(n => new NationState { Nation = n, ControllerId = controllerFor(n) })
            .ToList(),
        Players = new List<Player>(),
        Units = new List<Unit>(),
        TerritoryStates = new List<TerritoryState>(),
        Actions = new List<GameAction>()
    };

    /// <summary>
    /// The pure rotation rule, with every nation governed. Nothing may be skipped.
    /// </summary>
    [Fact]
    public void AdvanceTurnVisitsEveryGovernedNationInOrder()
    {
        var owner = Guid.NewGuid();
        var game = BuildGame(_ => owner);

        var visited = new List<Nation>();
        for (int i = 0; i < 12; i++)
        {
            visited.Add(game.CurrentTurnNation);
            game.AdvanceTurn();
        }

        var expected = new[]
        {
            Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe,
            Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe
        };
        Assert.Equal(expected, visited);
    }

    /// <summary>
    /// A nation with no government IS skipped, and the rest keep their order. This is the one sanctioned
    /// skip, so it must keep working - a fix for a drop-out must not be "never skip anything".
    /// </summary>
    [Fact]
    public void AnUngovernedNationIsSkippedAndTheRestKeepOrder()
    {
        var owner = Guid.NewGuid();
        var game = BuildGame(n => n == Nation.India ? null : owner);

        var visited = new List<Nation>();
        for (int i = 0; i < 5; i++)
        {
            visited.Add(game.CurrentTurnNation);
            game.AdvanceTurn();
        }

        Assert.Equal(new[] { Nation.Russia, Nation.China, Nation.Brazil, Nation.USA, Nation.Europe }, visited);
    }
}
