using System;
using Imperial2030.Server.Services;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the reward-shaping curriculum added for RL-4, and the ordering bug that motivated it: shaping
/// terms were once accumulated into a local that was folded into the reward near the top of the step
/// handler and then written to for another sixty lines. Two Investor penalties (up to -80 for covering a
/// nation's interest shortfall, -20 for missing one's own interest) were computed, logged as applied, and
/// thrown away for the whole of RL-3's training - nothing observable failed. ShapingReward now makes
/// that a thrown exception instead of a silent loss.
/// </summary>
public class TrainingRewardCurriculumTests
{
    [Fact]
    public void AShapingTermAddedAfterTheFoldThrowsInsteadOfBeingDropped()
    {
        var shaping = new ShapingReward();
        shaping.Add(16f);
        shaping.Add(-7f);

        Assert.Equal(9f * 0.5f, shaping.Fold(0.5f));

        var late = Assert.Throws<InvalidOperationException>(() => shaping.Add(-20f));
        Assert.Contains("after the fold", late.Message);
        Assert.Throws<InvalidOperationException>(() => shaping.Fold(1f));
    }

    /// <summary>
    /// The terminal signal - the final margin and the flat win/loss bonus - is the objective and is not
    /// part of the shaping that the curriculum scales down: with shaping at 0 the agent still gets it in
    /// full, which is what makes decaying the shaping make winning RELATIVELY more important.
    /// </summary>
    [Fact]
    public void TheTerminalWinLossRewardIsNotScaledByTheShapingCurriculum()
    {
        var shaping = new ShapingReward();
        shaping.Add(50f);
        float reward = shaping.Fold(0f) + TcpTrainingServer.TerminalReward(rlScore: 60, maxOfOthersScore: 45);

        Assert.Equal(115f, reward); // margin 15 + 100 for the win, none of the 50 shaping
        Assert.Equal(-110f, TcpTrainingServer.TerminalReward(rlScore: 45, maxOfOthersScore: 55));
        Assert.Equal(0f, TcpTrainingServer.TerminalReward(rlScore: 50, maxOfOthersScore: 50)); // a tie is neither
    }

    /// <summary>
    /// A client that predates the curriculum sends no scales at all. It must train on exactly the reward
    /// function it always did, which means both scales default to a no-op 1.0 (rule #17's spirit: an
    /// additive protocol change may not alter behaviour for anything that does not opt in).
    /// </summary>
    [Fact]
    public void CurriculumScalesDefaultToNoOp()
    {
        var session = new TcpTrainingServer.TrainingSession();

        Assert.Equal(1.0f, session.ShapingScale);
        Assert.Equal(1.0f, session.FactoryPenaltyScale);
    }

    /// <summary>
    /// The reason the build reward was raised from 10 to 16.
    ///
    /// A nation normally gets two builds per game - four home cities holding one factory each
    /// (Imperial-2030-Rules.pdf p.7), two already built at setup (p.4) - plus any it rebuilds after an
    /// enemy destroys one with three armies (p.11), which is uncommon. So over a nation-stint the agent
    /// gets roughly two chances to be paid and a long tail of chances to be punished for landing on a
    /// slot that usually can no longer do anything. For visiting Factory to be a
    /// rational gamble at all, a successful build has to be worth more than a wasted landing costs -
    /// otherwise the expected value of the slot is negative even at even odds, and avoiding it outright
    /// is the correct policy. Which is precisely what RL-3 learned.
    /// </summary>
    [Fact]
    public void ASuccessfulBuildOutweighsAWastedLanding()
    {
        float wastedWorstCase = TcpTrainingServer.WastedFactoryActionPenalty + TcpTrainingServer.AllFactoriesBuiltPenalty;

        Assert.True(TcpTrainingServer.FactoryBuildReward > wastedWorstCase,
            $"A build pays {TcpTrainingServer.FactoryBuildReward} but the worst wasted landing costs " +
            $"{wastedWorstCase}, so landing on Factory is negative expected value at even odds and the " +
            "agent is correct to avoid the slot entirely.");
    }

    /// <summary>
    /// The skip penalty must stay well above the build reward: standing on Factory, able to build, and
    /// declining is never the better option. This is the one factory penalty that cannot cause slot
    /// avoidance (it only fires once the agent is already there and CAN build), so it is free to be large.
    /// </summary>
    [Fact]
    public void DecliningAnAvailableBuildStaysWorseThanBuilding()
    {
        Assert.True(TcpTrainingServer.AvoidableFactorySkipPenalty > TcpTrainingServer.FactoryBuildReward,
            "Skipping an available build must cost more than building earns, or skipping becomes rational.");
    }
}
