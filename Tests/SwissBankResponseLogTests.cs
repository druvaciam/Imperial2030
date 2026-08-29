using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// How a Swiss Bank force-stop decision is attributed in the game log.
///
/// Observed live, two entries one second apart:
///
///   Bot Charlie (RL-3)  Europe  chose to PASS on forcing Europe to stop
///   Bot Echo (Default)  Europe  moved to Import from Maneuver (Cost: 0M)
///
/// which reads as though two different players own Europe. Only Bot Echo does. Bot Charlie is a Swiss
/// Bank holder answering a prompt *about* Europe, and the entry was tagged with the nation being forced.
///
/// Every other entry uses Nation to mean "the nation this player is acting as", and the terminal renders
/// it as a tag immediately after the player's name — so borrowing the field for "the nation this concerns"
/// reads as ownership. It is definitionally wrong here too: a Swiss Bank holder controls zero nations
/// (Imperial-2030-Rules.pdf p.12, "If a player does not control any government, he gets a Swiss Bank
/// instead"), so there is no nation they could be acting as.
///
/// The nation stays in the metadata, which is what the message is built from, so the line still reads
/// "chose to PASS on forcing Europe to stop" — it just no longer claims Charlie owns Europe.
/// </summary>
public class SwissBankResponseLogTests
{
    private static Game NewGame() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Swiss Bank Log",
        Status = GameStatus.InProgress,
        Actions = new List<GameAction>()
    };

    private static SwissBankResponseMetadata MetadataOf(GameAction action) =>
        JsonSerializer.Deserialize<SwissBankResponseMetadata>(
            action.Metadata!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void PassingOnAForceStopIsNotAttributedToTheForcedNation()
    {
        var game = NewGame();

        GameLogger.LogSwissBankPass(null, game, Nation.Europe, "Bot Charlie (RL-3)");

        var action = Assert.Single(game.Actions);
        Assert.Equal("SwissBankResponse", action.ActionType);
        Assert.Equal("Bot Charlie (RL-3)", action.PlayerName);
        Assert.Null(action.Nation);
    }

    [Fact]
    public void ForcingAStopIsNotAttributedToTheForcedNation()
    {
        var game = NewGame();

        GameLogger.LogSwissBankForceStop(null, game, Nation.Europe, "Bot Charlie (RL-3)");

        var action = Assert.Single(game.Actions);
        Assert.Null(action.Nation);
    }

    /// <summary>
    /// The nation must survive in the metadata: it is what GameTerminal builds the message from
    /// (`L["SwissBankPass", Names.Nation(meta.Nation)]`), so dropping it would blank the line instead of
    /// just removing the misleading tag.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheForcedNationIsStillRecordedInMetadata(bool forceStop)
    {
        var game = NewGame();

        if (forceStop)
        {
            GameLogger.LogSwissBankForceStop(null, game, Nation.Europe, "Bot Charlie (RL-3)");
        }
        else
        {
            GameLogger.LogSwissBankPass(null, game, Nation.Europe, "Bot Charlie (RL-3)");
        }

        var meta = MetadataOf(Assert.Single(game.Actions));
        Assert.Equal(Nation.Europe, meta.Nation);
        Assert.Equal(forceStop, meta.IsForceStop);
    }

    /// <summary>
    /// The contrast that makes the rule clear: a nation's own controller acting on the rondel IS acting
    /// as that nation, so that entry keeps its tag. Only the Swiss Bank responder loses it.
    /// </summary>
    [Fact]
    public void ARondelMoveByTheControllerKeepsItsNationTag()
    {
        var game = NewGame();

        GameLogger.LogRondelMove(null, game, targetSlot: 5, currentSlot: 3, cost: 0,
            nation: Nation.Europe, playerName: "Bot Echo (Default)");

        var action = Assert.Single(game.Actions);
        Assert.Equal(Nation.Europe, action.Nation);
        Assert.Equal("Bot Echo (Default)", action.PlayerName);
    }
}
