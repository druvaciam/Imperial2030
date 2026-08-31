using System;
using System.IO;
using System.Linq;
using Imperial2030.Server.Services;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the reward-shaping curriculum added for RL-4, and the ordering bug that motivated it.
///
/// The bug: HandleStepAsync accumulated shaping into `explicitBonusReward`, folded it into `reward` at
/// the top, and then kept subtracting from `explicitBonusReward` for another sixty lines. The two
/// Investor penalties down there — up to -80 for personally covering a nation's interest shortfall, and
/// -20 for missing one's own interest — were computed, logged as "[RL PENALTY]", and thrown away. The
/// training logs said they applied; the agent never saw them. Nothing failed, which is exactly why this
/// needs a guard: a discarded reward term is invisible from the outside.
///
/// The structural fix is that shaping is now folded in exactly once, at a single point after every
/// shaping term has been computed. That is an ordering property of the source, not of any value a unit
/// test can observe, so the first test below reads the file. Source-scanning is a blunt instrument and
/// deliberately used for just this one invariant.
/// </summary>
public class TrainingRewardCurriculumTests
{
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Imperial2030.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "Could not locate the repository root (no Imperial2030.sln found above the test assembly).");
        return dir!.FullName;
    }

    private static string[] TrainingServerSource() =>
        File.ReadAllLines(Path.Combine(FindRepositoryRoot(), "Server", "Services", "TcpTrainingServer.cs"));

    [Fact]
    public void NoShapingTermIsComputedAfterItHasBeenFoldedIntoTheReward()
    {
        var lines = TrainingServerSource();

        int fold = Array.FindIndex(lines, l => l.Contains("reward += explicitBonusReward * session.ShapingScale;"));
        Assert.True(fold >= 0,
            "Could not find the single fold point 'reward += explicitBonusReward * session.ShapingScale;'. " +
            "If it was renamed, update this test rather than deleting it - it guards a bug that silently " +
            "discarded two Investor penalties for the whole of RL-3's training.");

        var stragglers = lines
            .Select((text, index) => (text, index))
            .Where(x => x.index > fold)
            .Where(x => x.text.Contains("explicitBonusReward +=") || x.text.Contains("explicitBonusReward -="))
            .Select(x => $"line {x.index + 1}: {x.text.Trim()}")
            .ToList();

        Assert.True(stragglers.Count == 0,
            "These shaping terms are computed AFTER explicitBonusReward was folded into reward, so they " +
            "have no effect on training and will be silently discarded:\n  " + string.Join("\n  ", stragglers));
    }

    /// <summary>
    /// The terminal signal — the final VP margin and the flat win/loss bonus — must sit after the fold so
    /// that decaying the shaping scale makes winning RELATIVELY more important. Scaling it too would
    /// defeat the entire point of the decay.
    /// </summary>
    [Fact]
    public void TheTerminalWinLossRewardIsNotScaledByTheShapingCurriculum()
    {
        var lines = TrainingServerSource();

        int fold = Array.FindIndex(lines, l => l.Contains("reward += explicitBonusReward * session.ShapingScale;"));
        int win = Array.FindIndex(lines, l => l.Contains("reward += 100f;"));
        int loss = Array.FindIndex(lines, l => l.Contains("reward -= 100f;"));

        Assert.True(win > fold, "The +100 win bonus must be applied after the shaping fold, unscaled.");
        Assert.True(loss > fold, "The -100 loss penalty must be applied after the shaping fold, unscaled.");
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
