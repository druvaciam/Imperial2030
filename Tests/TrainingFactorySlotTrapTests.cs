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
/// The training server's per-turn decisions (Factory build/skip, Import placements) are owed for the turn
/// that landed on the slot, and only then. The rondel move records them in the session - they are never
/// derived from <c>RondelPosition</c>, which persists across turns, together with a flag that
/// <see cref="Game.AdvanceTurn"/> clears every turn: that pair would say "decision owed" on every later
/// turn of the nation, each agent step would be consumed as a decision already made, the mask would
/// never offer a rondel move, and the nation could not leave the slot for the rest of the game.
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
                    // The RL agent holds China; every other nation is a heuristic bot's.
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
    /// The decision is owed on the turn the nation lands on the slot.
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
    /// The nation lands on Factory, resolves the decision without building (no treasury), its turn ends,
    /// the other five nations play, and its turn comes round again - at which point it must be asked for a
    /// RONDEL MOVE, not for the same factory decision a second time.
    /// </summary>
    [Fact]
    public void ANationDoesNotOweTheSameFactoryDecisionOnItsNextTurn()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        // Turn 1: the agent's rondel move lands China on the Factory slot, so a decision is owed.
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
            "China was asked for the SAME factory decision again on its next turn: each step would be " +
            "consumed as a decision already made and the nation would never leave the slot.");
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
    /// Every landing on the Factory slot gets its own build/skip decision. The rondel move that lands
    /// there sets <c>FactoryDecisionOwedBy</c>, and making the decision clears it - so a nation that
    /// leaves the slot and comes back on a later turn is asked again.
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
        session.FactoryDecisionOwedBy = Nation.China; // what the rondel move sets on landing here
        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId));
    }

    /// <summary>
    /// FactoryDecisionOwedBy names one nation: the RL agent controls several nations over a game (in the
    /// exported one, all four that stopped acting). Setting it for China must not make Brazil owe a decision.
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
    /// Landing on Factory by way of the Investor space (Maneuver 1 -> Factory is six spaces, crossing it)
    /// activates the Investor turn, and Imperial-2030-Rules.pdf p.11 resolves that as part of the
    /// MOVEMENT, before the nation acts on its destination. The decision is still owed - but not until the
    /// Investor turn has cleared.
    /// </summary>
    [Fact]
    public void TheDecisionWaitsForAnOpenInvestorPhase()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);

        china.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.China;
        game.IsInvestorTurn = true;

        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId),
            "The factory decision was offered while the Investor turn the move triggered was still open.");

        game.IsInvestorTurn = false;
        Assert.True(TcpTrainingServer.IsFactoryDecisionPending(session, china, rlPlayerId),
            "Once the Investor turn clears the decision is owed again - it must not have been forgotten.");
    }

    /// <summary>
    /// The same order applies to the Import sequence: a move to Import that crossed the Investor space
    /// neither offers placements nor ends the turn until the Investor turn has cleared.
    /// </summary>
    [Fact]
    public void TheImportSequenceWaitsForAnOpenInvestorPhase()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);
        china.RondelPosition = RondelData.ImportSlot;
        china.Treasury = 3;

        session.PendingImportRemaining = 3;
        session.ImportSequenceNation = Nation.China;
        game.IsInvestorTurn = true;

        Assert.False(TcpTrainingServer.IsImportSequencePending(session, game),
            "Import placements were offered while the Investor turn the move triggered was still open.");
        Assert.False(TcpTrainingServer.MayRondelActionEndTheTurn(game),
            "The turn was ended while the Investor turn the move triggered was still open.");

        game.IsInvestorTurn = false;
        Assert.True(TcpTrainingServer.IsImportSequencePending(session, game));
        Assert.True(TcpTrainingServer.MayRondelActionEndTheTurn(game));
    }

    /// <summary>
    /// The Import sequence belongs to the nation that landed on Import, for that turn. If a rival's
    /// purchase in the Investor turn that move opened takes the government (p.12), the bot finishes the
    /// turn and the rotation moves on - the sequence must not then be offered to the RL agent's next nation.
    /// </summary>
    [Fact]
    public void TheImportSequenceDoesNotOutliveTheNationThatStartedIt()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var china = game.NationStates.First(n => n.Nation == Nation.China);
        china.RondelPosition = RondelData.ImportSlot;
        china.Treasury = 3;

        session.PendingImportRemaining = 3;
        session.ImportSequenceNation = Nation.China;
        Assert.True(TcpTrainingServer.IsImportSequencePending(session, game));

        // The government changes hands during the Investor turn and the turn moves on without the RL agent finishing.
        game.AdvanceTurn();
        Assert.Equal(Nation.India, game.CurrentTurnNation);

        Assert.False(TcpTrainingServer.IsImportSequencePending(session, game),
            "China's unfinished Import sequence was offered to India.");
    }

    /// <summary>
    /// A nation the RL agent does not control is never asked, whatever FactoryDecisionOwedBy says - those are played by
    /// BotService, and they are the ones that kept moving all game.
    /// </summary>
    [Fact]
    public void ANationTheRLAgentDoesNotControlIsNeverAsked()
    {
        var rlPlayerId = Guid.NewGuid();
        var (game, session) = BuildGame(rlPlayerId);
        var india = game.NationStates.First(n => n.Nation == Nation.India);

        india.RondelPosition = RondelData.FactorySlot;
        session.FactoryDecisionOwedBy = Nation.India;

        Assert.False(TcpTrainingServer.IsFactoryDecisionPending(session, india, rlPlayerId));
    }
}
