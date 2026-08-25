using System;
using System.Linq;

namespace Imperial2030.Shared.Constants;

/// <summary>
/// The Taxation step's revenue rules, in one place.
///
/// This arithmetic was previously re-derived at five separate call sites (TaxationHelper's preview
/// and apply paths, the Rondel tooltip, the Nations panel preview, and the RL reward shaping), each
/// with the numbers 2 / 1 / 15 / 23 written out by hand. One of them had already drifted: the Rondel
/// tooltip omitted the flag cap entirely.
///
/// Only the *rules* live here. Each caller still counts its own factories and flags, because they
/// read from different models — the server from EF entities, the client from DTOs.
///
/// Source: Imperial-2030-Rules.pdf, "Taxation" (p.12).
/// </summary>
public static class TaxationRules
{
    /// <summary>Each factory with no hostile army in its province yields this much.</summary>
    public const int RevenuePerUnblockedFactory = 2;

    /// <summary>Each controlled region yields this much.</summary>
    public const int RevenuePerFlag = 1;

    /// <summary>Every nation has exactly four home provinces, so at most four factories.</summary>
    public const int HomeProvincesPerNation = 4;

    /// <summary>
    /// A nation only owns 15 flags, so it can never hold more than 15 regions — see
    /// ManeuverController's flag-placement check, which enforces the same limit on the board.
    /// </summary>
    public const int MaxFlagsPerNation = 15;

    /// <summary>
    /// The rulebook's 23M ceiling, written as its derivation rather than a bare literal:
    /// 4 factories x 2M + 15 flags x 1M. Kept as a computed constant so the relationship stays
    /// visible and the three inputs cannot drift out of sync with the total.
    /// </summary>
    public const int MaxRevenue =
        HomeProvincesPerNation * RevenuePerUnblockedFactory + MaxFlagsPerNation * RevenuePerFlag;

    /// <summary>The treasury pays this per army and per fleet in soldiers' pay.</summary>
    public const int SoldiersPayPerUnit = 1;

    /// <summary>
    /// Total tax revenue for a nation, given how many of its factories are unblocked and how many
    /// regions it controls. Flags are capped before the total is, which matters when a nation holds
    /// many regions but few working factories: capping only the total would over-report revenue.
    /// </summary>
    public static int ComputeRevenue(int unblockedFactoryCount, int controlledRegionCount)
    {
        int factoryRevenue = unblockedFactoryCount * RevenuePerUnblockedFactory;
        int flagRevenue = Math.Min(MaxFlagsPerNation, controlledRegionCount) * RevenuePerFlag;
        return Math.Min(MaxRevenue, factoryRevenue + flagRevenue);
    }

    /// <summary>Soldiers' pay owed for a given number of armies plus fleets.</summary>
    public static int ComputeSoldiersPay(int unitCount) => unitCount * SoldiersPayPerUnit;

    /// <summary>
    /// The government's success bonus, before it is capped at what the treasury can actually afford.
    ///
    /// Standard rules (p.12, "3. Success bonus"): read straight off the tax chart — "1 million with at
    /// least a tax revenue of 6 million, 2 million with at least a tax revenue of 10 million etc."
    ///
    /// Under the house variant (<c>Game.VariantBonusOnlyForTaxIncreases</c>) the bonus is instead the
    /// increase in power-gain tier since the nation's last taxation, so holding steady pays nothing and a
    /// decline never pays out negatively.
    ///
    /// Only the rule lives here, as with the rest of this class: the caller supplies the previous and
    /// current revenue, and applies its own treasury cap afterwards. This block was previously written
    /// out identically in both <c>TaxationHelper.PreviewTaxation</c> and <c>ApplyTaxation</c>.
    /// </summary>
    public static int ComputeSuccessBonus(bool variantBonusOnlyForTaxIncreases, int previousTaxRevenue, int totalTaxRevenue)
    {
        if (!variantBonusOnlyForTaxIncreases) return TaxChart.GetStandardBonus(totalTaxRevenue);

        int oldTier = TaxChart.GetPowerGain(previousTaxRevenue);
        int newTier = TaxChart.GetPowerGain(totalTaxRevenue);
        return Math.Max(0, newTier - oldTier);
    }

    /// <summary>
    /// Factories of <paramref name="nation"/> that can be taxed: built in one of its own home
    /// provinces, with no hostile army standing there.
    ///
    /// Defined over the DTO because both client callers (the Rondel tooltip and the Nations panel)
    /// need it and neither can see the server's EF entities. The server keeps its own entity-based
    /// counter; only the counting differs, the revenue rules above are shared by all of them.
    /// </summary>
    public static int CountUnblockedFactories(Models.GameDetailDto game, Models.Nation nation)
    {
        int count = 0;
        foreach (var ts in game.Territories)
        {
            if (!ts.HasFactory) continue;

            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (territoryDef == null || territoryDef.Nation != nation) continue;

            bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == ts.TerritoryId
                && u.UnitType == Models.UnitType.Army && u.Nation != nation && u.IsHostile);
            if (!hasHostileArmy) count++;
        }
        return count;
    }
}
