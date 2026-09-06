using Imperial2030.Server.Services;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The limits that halt a pathological training session.
///
/// Halting THROWS, deliberately. A session hitting either limit is a bug whose cause is not yet known,
/// and ending the episode quietly so training carries on would hide it. Observed live: one session hit
/// the step limit, and the resulting six "connection forcibly closed" errors 40ms apart look like a
/// client problem while actually being the server hanging up.
///
/// Note the two limits catch different things and both are needed: the same run recorded zero
/// "stalled on turn" hits, so turns WERE advancing - the game was simply very long (>2000 steps against
/// a measured ep_len_mean of ~61), not stuck on one decision.
/// </summary>
public class TrainingSessionHaltTests
{
    [Fact]
    public void ANormalLengthEpisodeIsNotHalted()
    {
        Assert.Null(TcpTrainingServer.SessionHaltReason(totalSteps: 61, consecutiveSameTurnSteps: 3));
    }

    [Fact]
    public void AtTheLimitIsStillAllowed()
    {
        Assert.Null(TcpTrainingServer.SessionHaltReason(
            TcpTrainingServer.MaxSessionSteps, TcpTrainingServer.MaxConsecutiveSameTurnSteps));
    }

    [Fact]
    public void AnOverlongEpisodeIsHalted()
    {
        var reason = TcpTrainingServer.SessionHaltReason(
            TcpTrainingServer.MaxSessionSteps + 1, consecutiveSameTurnSteps: 0);

        Assert.NotNull(reason);
        Assert.Contains("without finishing", reason);
    }

    /// <summary>
    /// A decision loop that re-derives the same candidate forever. Distinct from the case above, and
    /// distinguishable in the message so a live log says which one happened.
    /// </summary>
    [Fact]
    public void ATurnThatNeverAdvancesIsHalted()
    {
        var reason = TcpTrainingServer.SessionHaltReason(
            totalSteps: 100, consecutiveSameTurnSteps: TcpTrainingServer.MaxConsecutiveSameTurnSteps + 1);

        Assert.NotNull(reason);
        Assert.Contains("without advancing the turn", reason);
    }

}
