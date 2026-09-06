using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace Imperial2030.Tests;

/// <summary>
/// The reason four nations stopped moving in training game b53a01ef.
///
/// MEASURED, from that game's export (9,471 actions): every nation the RL trainee controlled - Russia,
/// China, Brazil, Europe - fell silent, and for all four the last action logged was a rondel Move to
/// TargetSlot 1, which is <see cref="RondelData.FactorySlot"/>. India and USA, which stayed with heuristic
/// bots, alternated normally for the remaining ~7,500 entries. The rotation itself was intact
/// (China -> India -> Brazil -> USA -> Europe -> Russia, then the same cycle minus China, then minus
/// Brazil, ...), so those nations were still being given turns; the turns simply produced nothing.
///
/// THE TRAP: the training server decided "the agent owes me a factory decision" from
/// <c>RondelPosition == FactorySlot &amp;&amp; !HasBuiltThisTurn</c>. RondelPosition persists across turns -
/// that is what a rondel is - while <see cref="Game.AdvanceTurn"/> clears HasBuiltThisTurn at the start of
/// every turn. So the condition re-armed on the nation's NEXT turn, and the one after, forever: each turn
/// the agent's action was consumed as another factory decision, the action mask offered only
/// build/skip (never a rondel move), nothing was logged, and the nation could never leave the slot. The
/// single escape was actually building - which needs 5M and a free home city - so a nation that ran out of
/// either was stuck for the rest of the game.
///
/// <see cref="Server.Services.BotService"/> is immune and shows the correct shape: it gates on the
/// <c>targetSlot</c> the nation moved to ON THIS TURN (BotService.cs, <c>buildPending</c>), not on the
/// persistent RondelPosition. The Import sequence in the training server is immune for the same reason -
/// it arms <c>session.PendingImportRemaining</c> at arrival instead of re-deriving it from the rondel.
/// </summary>
public class TrainingFactorySlotTrapTests
{
    private readonly ITestOutputHelper _output;

    public TrainingFactorySlotTrapTests(ITestOutputHelper output) => _output = output;

    private static (Game Game, TcpTrainingServer.TrainingSession Session) BuildGame(Guid rlPlayerId)
    {
        var botId = Guid.NewGuid();
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "RL_Training_FactorySlot",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.China,
            Players = new List<Player>
            {
                new() { Id = rlPlayerId, IsBot = true, BotType = "RL", BotName = "RL-4Agent" },
                new() { Id = botId, IsBot = true, BotType = "Default", BotName = "RL Bot 1" }
            },
            NationStates = Enum.GetValues<Nation>()
                .Select(n => new NationState
                {
                    Nation = n,
                    // The trainee holds China; every other nation is a heuristic bot's, exactly as in the
                    // exported game where only the trainee's nations went silent.
                    ControllerId = n == Nation.China ? rlPlayerId : botId,
                    Treasury = 0,
                    RondelPosition = 0
                }).ToList(),
            Units = new List<Unit>(),
            TerritoryStates = new List<TerritoryState>(),
            Actions = new List<GameAction>()
        };

        return (game, new TcpTrainingServer.TrainingSession { Game = game, RLPlayerId = rlPlayerId });
    }

    /// <summary>
    /// The decision is owed on the turn the nation lands on the slot. That must keep working - the fix
    /// must not be "never ask for a factory decision".
    /// </summary>
    [Fact]
    public void ArrivingOnTheFactorySlotOwesADecision()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        china.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.China;

        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));
    }

    /// <summary>
    /// The regression. The nation lands on Factory, resolves the decision without building (no treasury),
    /// its turn ends, the other five nations play, and its turn comes round again - at which point it must
    /// be asked for a RONDEL MOVE, not for the same factory decision a second time.
    ///
    /// Before the fix this asserted false: AdvanceTurn had cleared HasBuiltThisTurn while RondelPosition
    /// still read FactorySlot, so the gate re-armed and the nation never moved again.
    /// </summary>
    [Fact]
    public void ANationDoesNotOweTheSameFactoryDecisionOnItsNextTurn()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        // Turn 1: the agent's rondel move lands China on the Factory slot and the decision is armed.
        china.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.China;
        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));

        // The agent resolves it. With a 0M treasury it cannot build, so this is the skip path - which is
        // exactly the case that trapped Russia, China, Brazil and Europe.
        china.HasBuiltThisTurn = true;
        session.FactoryDecisionOwedBy = null;
        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));

        // The turn ends and the rondel carries China's position forward, as a rondel does.
        game.AdvanceTurn();
        Assert.False(china.HasBuiltThisTurn); // AdvanceTurn cleared it - the other half of the trap
        Assert.Equal(RondelData.FactorySlot, china.RondelPosition);

        // Round the table until it is China's turn again.
        var order = new List<Nation> { game.CurrentTurnNation };
        for (int i = 0; i < 10 && game.CurrentTurnNation != Nation.China; i++)
        {
            game.AdvanceTurn();
            order.Add(game.CurrentTurnNation);
        }
        _output.WriteLine("Rotation: " + string.Join(" -> ", order));
        Assert.Equal(Nation.China, game.CurrentTurnNation);

        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId),
            "China was asked for the SAME factory decision again on its next turn. RondelPosition is " +
            "still FactorySlot (rondel positions persist) and AdvanceTurn cleared HasBuiltThisTurn, so a " +
            "gate built from those two re-arms every turn: the agent's action is consumed as a factory " +
            "decision it already made, the mask never offers a rondel move, nothing is logged, and the " +
            "nation can never leave the slot. Measured in training game b53a01ef, where all four nations " +
            "the trainee controlled fell silent immediately after a rondel Move to TargetSlot 1.");
    }

    /// <summary>
    /// Two turns later, still on the slot, still not owed. The trap was permanent, so one turn of
    /// clearance is not enough to prove it gone.
    /// </summary>
    [Fact]
    public void TheNationStaysFreeOfTheDecisionOnEveryLaterTurn()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        china.RondelPosition = RondelData.FactorySlot;
        china.HasBuiltThisTurn = true;
        session.FactoryDecisionOwedBy = null;

        for (int lap = 0; lap < 3; lap++)
        {
            do { game.AdvanceTurn(); } while (game.CurrentTurnNation != Nation.China);

            Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId),
                $"China owed a factory decision again on lap {lap + 1} while sitting on the Factory slot.");
        }
    }

    /// <summary>
    /// A second visit to the slot IS a new decision. The fix arms the flag on arrival, so leaving and
    /// coming back must ask again - otherwise the agent could never build a factory after its first visit.
    /// </summary>
    [Fact]
    public void ComingBackToTheFactorySlotOwesAFreshDecision()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        china.RondelPosition = RondelData.FactorySlot;
        china.HasBuiltThisTurn = true;
        session.FactoryDecisionOwedBy = null;

        // Away to Taxation, then back round the rondel to Factory on a later turn.
        china.RondelPosition = RondelData.TaxationSlot;
        do { game.AdvanceTurn(); } while (game.CurrentTurnNation != Nation.China);
        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));

        china.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.China; // armed by the rondel move that landed here
        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));
    }

    /// <summary>
    /// The flag is per-nation, not per-session: the trainee controls several nations over a game (in the
    /// exported one, all four that died). Arming it for China must not make Brazil owe a decision.
    /// </summary>
    [Fact]
    public void TheDecisionIsScopedToTheNationThatLandedOnTheSlot()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);
        var brazil = game.NationStates.First(n => n.Nation == Nation.Brazil);
        brazil.ControllerId = rlPlayerId;
        brazil.RondelPosition = RondelData.FactorySlot;

        china.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.China;

        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));
        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, brazil, rlPlayerId));
    }

    /// <summary>
    /// A nation the trainee does not control is never asked, whatever the flag says - those are played by
    /// BotService, and they are the ones that kept moving all game.
    /// </summary>
    [Fact]
    public void ANationTheTraineeDoesNotControlIsNeverAsked()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var india = game.NationStates.First(n => n.Nation == Nation.India);

        india.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.India;

        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, india, rlPlayerId));
    }
}
