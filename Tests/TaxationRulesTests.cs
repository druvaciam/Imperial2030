using Imperial2030.Shared.Constants;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Pins the Taxation revenue arithmetic that TaxationRules centralises for all five callers
/// (TaxationHelper preview + apply, the Rondel tooltip, the Nations panel, RL reward shaping).
/// Values are from Imperial-2030-Rules.pdf, "Taxation".
/// </summary>
public class TaxationRulesTests
{
    [Fact]
    public void MaxRevenue_MatchesTheRulebookCeiling()
    {
        // The rulebook states 23M, derived as 4 factories x 2M + 15 flags x 1M. If any input
        // constant is edited, this catches the total silently disagreeing with the rules.
        Assert.Equal(23, TaxationRules.MaxRevenue);
    }

    [Theory]
    // factories, flags -> revenue
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 2)]      // 2M per unblocked factory
    [InlineData(0, 1, 1)]      // 1M per flag
    [InlineData(4, 5, 13)]     // 8 + 5, nothing capped
    [InlineData(4, 15, 23)]    // the rulebook's worked maximum
    public void ComputeRevenue_AddsFactoriesAndFlags(int factories, int flags, int expected)
    {
        Assert.Equal(expected, TaxationRules.ComputeRevenue(factories, flags));
    }

    [Theory]
    // Flags are capped at 15 BEFORE the 23 total cap. The Rondel tooltip used to skip the flag cap
    // and rely on the total cap alone; these are the inputs where that difference is visible.
    [InlineData(1, 20, 17)]    // capped-flags: 2 + 15 = 17. Without the flag cap it would read 22.
    [InlineData(0, 30, 15)]    // no factories at all: pure flag revenue, still capped at 15
    [InlineData(2, 16, 19)]    // 4 + 15 = 19, where total-cap-only would give 20
    public void ComputeRevenue_CapsFlagsBeforeTotal(int factories, int flags, int expected)
    {
        Assert.Equal(expected, TaxationRules.ComputeRevenue(factories, flags));

        // Demonstrate the divergence explicitly rather than just asserting the right number.
        int totalCapOnly = System.Math.Min(TaxationRules.MaxRevenue,
            factories * TaxationRules.RevenuePerUnblockedFactory + flags * TaxationRules.RevenuePerFlag);
        Assert.NotEqual(totalCapOnly, expected);
    }

    [Fact]
    public void ComputeRevenue_NeverExceedsTheCeiling()
    {
        // Exhaustive over every reachable board state: 0-4 unblocked factories, 0-63 regions.
        for (int f = 0; f <= TaxationRules.HomeProvincesPerNation; f++)
        {
            for (int flags = 0; flags <= 63; flags++)
            {
                int revenue = TaxationRules.ComputeRevenue(f, flags);
                Assert.InRange(revenue, 0, TaxationRules.MaxRevenue);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(16, 16)]
    public void ComputeSoldiersPay_IsOnePerUnit(int units, int expected)
    {
        Assert.Equal(expected, TaxationRules.ComputeSoldiersPay(units));
    }

    // --- Success bonus (step 3 of Taxation) ---------------------------------------------------------
    // Previously written out twice, identically, in TaxationHelper's preview and apply paths.

    [Theory]
    // Imperial-2030-Rules.pdf p.12, "3. Success bonus": "1 million with at least a tax revenue of
    // 6 million, 2 million with at least a tax revenue of 10 million etc."
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(12, 3)]
    [InlineData(14, 4)]
    [InlineData(16, 5)]
    public void ComputeSuccessBonus_StandardRules_ReadsTheTaxChart(int totalTaxRevenue, int expected)
    {
        // previousTaxRevenue is irrelevant outside the variant, so vary it to prove it is ignored.
        Assert.Equal(expected, TaxationRules.ComputeSuccessBonus(false, previousTaxRevenue: 0, totalTaxRevenue));
        Assert.Equal(expected, TaxationRules.ComputeSuccessBonus(false, previousTaxRevenue: 23, totalTaxRevenue));
    }

    [Theory]
    // House variant (Game.VariantBonusOnlyForTaxIncreases): the bonus is the GAIN in power-gain tier,
    // so standing still pays nothing and only genuine growth is rewarded.
    [InlineData(0, 6, 1)]    // tier 0 -> 1
    [InlineData(6, 6, 0)]    // no change
    [InlineData(6, 10, 2)]   // tier 1 -> 3
    [InlineData(10, 6, 0)]   // a DROP never pays out, and never goes negative
    [InlineData(0, 18, 10)]  // tier 0 -> 10
    public void ComputeSuccessBonus_VariantRules_PaysOnlyTheIncreaseInTier(int previousTaxRevenue, int totalTaxRevenue, int expected)
    {
        Assert.Equal(expected, TaxationRules.ComputeSuccessBonus(true, previousTaxRevenue, totalTaxRevenue));
    }
}
