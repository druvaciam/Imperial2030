using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// A rondel move that walks the marker past Taxation gives up the taxation it could have stopped on -
/// and the space it passed is nearer than the one it paid to reach, so it was affordable by definition
/// (Imperial-2030-Rules.pdf p.6: the first three spaces are free and each further space costs more).
/// The reward function penalises wasted visits to Factory, Production, Import and Maneuver but had
/// nothing for this, which is the shape of the losses measured for RL-5: 25 of 62 chances at a 5+ power
/// taxation went elsewhere, ~30 power a game.
/// </summary>
public class TrainingSkippedTaxationTests
{
    private const int Taxation = RondelData.TaxationSlot;      // 0
    private const int Factory = RondelData.FactorySlot;        // 1
    private const int Production1 = RondelData.ProductionSlot1;// 2
    private const int Maneuver1 = RondelData.ManeuverSlot1;    // 3
    private const int Investor = RondelData.InvestorSlot;      // 4
    private const int Import = RondelData.ImportSlot;          // 5
    private const int Maneuver2 = RondelData.ManeuverSlot2;    // 7

    [Fact]
    public void WalkingPastAValuableTaxationIsPenalised()
    {
        // Import -> Maneuver 1 is five spaces and passes Taxation on the way.
        Assert.True(TcpTrainingServer.SkippedValuableTaxation(Import, Maneuver1, expectedTaxPowerGain: 5));
        Assert.True(TcpTrainingServer.SkippedValuableTaxation(Maneuver2, Factory, expectedTaxPowerGain: 9));
    }

    [Fact]
    public void StoppingOnTaxationIsNotSkippingIt()
    {
        Assert.False(TcpTrainingServer.SkippedValuableTaxation(Import, Taxation, expectedTaxPowerGain: 8));
    }

    [Fact]
    public void AMoveThatNeverReachesTaxationIsNotSkippingIt()
    {
        // Production 1 -> Investor stops two spaces short of Taxation.
        Assert.False(TcpTrainingServer.SkippedValuableTaxation(Production1, Investor, expectedTaxPowerGain: 8));
        // Leaving Taxation is not passing it: the marker has to go all the way round to reach it again.
        Assert.False(TcpTrainingServer.SkippedValuableTaxation(Taxation, Maneuver1, expectedTaxPowerGain: 8));
    }

    /// <summary>
    /// Production sits on two spaces (2 and 6) and Maneuver on two (3 and 7), so one of each pair is
    /// always nearer. Paying to reach the farther one buys the same action for more money. Seen live:
    /// China paid 3M for Taxation -> Production 2 with Production 1 two spaces away and free.
    /// </summary>
    [Fact]
    public void PayingToReachTheFartherOfTwoIdenticalSpacesIsPenalised()
    {
        Assert.True(TcpTrainingServer.PaidForTheFartherTwinSlot(Taxation, RondelData.ProductionSlot2, power: 4));
        Assert.True(TcpTrainingServer.PaidForTheFartherTwinSlot(Factory, Maneuver2, power: 0));
    }

    [Fact]
    public void ReachingTheNearerOfTheTwoIsFine()
    {
        Assert.False(TcpTrainingServer.PaidForTheFartherTwinSlot(Taxation, Production1, power: 4));
        Assert.False(TcpTrainingServer.PaidForTheFartherTwinSlot(Factory, Maneuver1, power: 0));
    }

    [Fact]
    public void TheFartherOneIsFineWhenItCostsNoMoreOrTheNearerIsWhereTheMarkerStands()
    {
        // Both within the three free spaces (p.6): the farther one is no more expensive.
        Assert.False(TcpTrainingServer.PaidForTheFartherTwinSlot(Maneuver1, RondelData.ProductionSlot2, power: 0));
        // Standing on Production 1: it is not an alternative, staying is not a move.
        Assert.False(TcpTrainingServer.PaidForTheFartherTwinSlot(Production1, RondelData.ProductionSlot2, power: 9));
        // Not one of the paired spaces at all.
        Assert.False(TcpTrainingServer.PaidForTheFartherTwinSlot(Maneuver2, Import, power: 9));
    }

    [Fact]
    public void TaxationWorthLittleIsNotWorthStoppingFor()
    {
        Assert.False(TcpTrainingServer.SkippedValuableTaxation(Import, Maneuver1, expectedTaxPowerGain: 4));
        Assert.False(TcpTrainingServer.SkippedValuableTaxation(Import, Maneuver1, expectedTaxPowerGain: 0));
        Assert.Equal(5, TcpTrainingServer.SkippedTaxationPowerThreshold);
    }
}
