using Imperial2030.Shared.Constants;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// The rondel move-cost rule, previously re-derived by hand at eleven call sites (server endpoints,
    /// BotService, the RL strategy, the training server and the Blazor client). Anchored on
    /// Imperial-2030-Rules.pdf p.6's own worked numbers so the shared implementation can be checked against
    /// the rulebook rather than against whatever the copies happened to agree on.
    /// </summary>
    public class RondelMoveCostTests
    {
        [Theory]
        // p.6: "At the beginning, the Power Factor for each nation is zero"
        [InlineData(0, 0)]
        [InlineData(4, 0)]
        // p.6: "If for example a nation has reached 17 power points and the Power Factor therefore amounts to 3"
        [InlineData(17, 3)]
        [InlineData(5, 1)]
        [InlineData(11, 2)]
        [InlineData(25, 5)]
        public void GetPowerFactor_MatchesTheScoringTrack(int power, int expectedFactor)
        {
            Assert.Equal(expectedFactor, RondelData.GetPowerFactor(power));
        }

        [Theory]
        // p.6: "The nation marker may be moved to one of the three spaces ahead at no cost".
        [InlineData(1, 0)]
        [InlineData(2, 0)]
        [InlineData(3, 0)]
        // "for each additional space past the first three ... (1 + Power Factor) in million".
        // Power 0 -> factor 0 -> 1M per additional space.
        [InlineData(4, 1)]
        [InlineData(5, 2)]
        [InlineData(6, 3)]
        public void GetMoveCost_AtZeroPower_ChargesOneMillionPerSpacePastTheFirstThree(int distance, int expectedCost)
        {
            int from = RondelData.TaxationSlot;
            int to = (from + distance) % RondelData.SlotCount;

            Assert.Equal(expectedCost, RondelData.GetMoveCost(from, to, power: 0));
        }

        [Fact]
        public void GetMoveCost_MatchesTheRulebooksChinaExample()
        {
            // p.6: "China has 11 power points and is standing on the Investor space on the rondel."
            // Investor(4) -> Taxation(0) is 4 spaces, i.e. one past the free three. Power Factor is
            // 11 / 5 = 2, so that one space costs 1 + 2 = 3 million.
            Assert.Equal(3, RondelData.GetMoveCost(RondelData.InvestorSlot, RondelData.TaxationSlot, power: 11));
        }

        [Fact]
        public void GetMoveCost_ScalesWithPowerFactorPastTheFreeDistance()
        {
            // 17 power -> factor 3 -> 4M per additional space; 6 spaces is 3 past the free three.
            Assert.Equal(12, RondelData.GetMoveCost(RondelData.TaxationSlot, RondelData.ProductionSlot2, power: 17));

            // ...and power still costs nothing inside the free distance.
            Assert.Equal(0, RondelData.GetMoveCost(RondelData.TaxationSlot, RondelData.ManeuverSlot1, power: 17));
        }

        [Fact]
        public void GetMoveCost_ForAMarkerNotYetOnTheRondel_IsFree()
        {
            // A nation that has never moved has no RondelPosition; its first placement is not a move and
            // every call site treated it as costing nothing.
            Assert.Equal(0, RondelData.GetMoveCost(null, RondelData.ImportSlot, power: 20));
        }

        [Theory]
        [InlineData(RondelData.TaxationSlot, RondelData.ManeuverSlot1, 3)]
        [InlineData(RondelData.InvestorSlot, RondelData.TaxationSlot, 4)]
        // Wraps clockwise past the end of the wheel rather than going backwards.
        [InlineData(RondelData.ManeuverSlot2, RondelData.TaxationSlot, 1)]
        public void GetMoveDistance_CountsClockwise(int from, int to, int expected)
        {
            Assert.Equal(expected, RondelData.GetMoveDistance(from, to));
        }
    }
}
