using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using System;
using System.Linq;

namespace Imperial2030.Server.Helpers;

public static class TaxationHelper
{
    public static (int ExpectedBonus, int ExpectedTreasuryGain, int ExpectedPowerGain, int TotalTaxRevenue, int SoldiersPay) PreviewTaxation(Game game, NationState nationState)
    {
        var (totalTaxRevenue, soldiersPay) = ComputeTaxNumbers(game, nationState.Nation);

        // Simulation of treasury changes
        int simulatedTreasury = nationState.Treasury + totalTaxRevenue;
        int actualPay = Math.Min(simulatedTreasury, soldiersPay);
        simulatedTreasury -= actualPay;

        int bonus = TaxationRules.ComputeSuccessBonus(game.VariantBonusOnlyForTaxIncreases, nationState.TaxRevenue, totalTaxRevenue);

        int actualBonus = Math.Min(simulatedTreasury, bonus);
        simulatedTreasury -= actualBonus;

        int powerGain = Imperial2030.Shared.Constants.TaxChart.GetPowerGain(totalTaxRevenue);
        
        int expectedTreasuryGain = simulatedTreasury - nationState.Treasury;

        return (actualBonus, expectedTreasuryGain, powerGain, totalTaxRevenue, soldiersPay);
    }

    public static bool IsProjectedWinningGameEndingTaxation(
        Game game,
        NationState nationState,
        Player controller)
    {
        var preview = PreviewTaxation(game, nationState);
        int projectedPower = Math.Min(
            GameConstants.MaxPowerPoints,
            nationState.Power + preview.ExpectedPowerGain);
        if (projectedPower < GameConstants.MaxPowerPoints) return false;

        int moveCost = RondelData.GetMoveCost(
            nationState.RondelPosition,
            RondelData.TaxationSlot,
            nationState.Power);

        int ProjectedPower(NationState state) => state.Nation == nationState.Nation
            ? projectedPower
            : state.Power;
        int ProjectedScore(Player player) => player.Cash
            + (player.Id == controller.Id ? preview.ExpectedBonus - moveCost : 0)
            + game.Bonds.Where(bond => bond.HolderId == player.Id).Sum(bond =>
            {
                var state = game.NationStates.FirstOrDefault(item => item.Nation == bond.Nation);
                return state == null ? 0 : bond.Interest * RondelData.GetPowerFactor(ProjectedPower(state));
            });

        var rankedNations = game.NationStates
            .OrderByDescending(ProjectedPower)
            .Select(state => state.Nation)
            .ToList();
        var scores = game.Players.ToDictionary(player => player.Id, ProjectedScore);
        var credits = game.Players.ToDictionary(player => player.Id, player => rankedNations.ToDictionary(
            nation => nation,
            nation => game.Bonds.Where(bond => bond.HolderId == player.Id && bond.Nation == nation).Sum(bond => bond.Cost)));

        var projectedWinner = game.Players.OrderBy(player => player, Comparer<Player>.Create((left, right) =>
        {
            int scoreDifference = scores[right.Id].CompareTo(scores[left.Id]);
            if (scoreDifference != 0) return scoreDifference;
            foreach (var nation in rankedNations)
            {
                int creditDifference = credits[right.Id][nation].CompareTo(credits[left.Id][nation]);
                if (creditDifference != 0) return creditDifference;
            }
            return 0;
        })).FirstOrDefault();

        return projectedWinner?.Id == controller.Id;
    }

    public static (int TotalTaxRevenue, int SoldiersPay, int Bonus, int PowerGain) ApplyTaxation(Game game, NationState nationState, Player controller)
    {
        var (totalTaxRevenue, soldiersPay) = ComputeTaxNumbers(game, nationState.Nation);

        nationState.Treasury += totalTaxRevenue;

        int actualPay = Math.Min(nationState.Treasury, soldiersPay);
        nationState.Treasury -= actualPay;

        int bonus = TaxationRules.ComputeSuccessBonus(game.VariantBonusOnlyForTaxIncreases, nationState.TaxRevenue, totalTaxRevenue);

        int actualBonus = Math.Min(nationState.Treasury, bonus);
        
        if (actualBonus > 0)
        {
            nationState.Treasury -= actualBonus;
            controller.Cash += actualBonus;
        }

        int powerGain = Imperial2030.Shared.Constants.TaxChart.GetPowerGain(totalTaxRevenue);
        nationState.Power += powerGain;
        if (nationState.Power > GameConstants.MaxPowerPoints) nationState.Power = GameConstants.MaxPowerPoints;

        nationState.PreviousTaxRevenue = nationState.TaxRevenue;
        nationState.TaxRevenue = totalTaxRevenue;

        return (totalTaxRevenue, actualPay, actualBonus, powerGain);
    }

    /// <summary>
    /// Steps 1 and 2 of Taxation for <paramref name="nation"/> — revenue in, soldiers' pay owed — read off
    /// the current board. Shared so the preview and the real thing cannot report different numbers: the
    /// preview exists precisely to tell the player what applying will do.
    /// </summary>
    private static (int TotalTaxRevenue, int SoldiersPay) ComputeTaxNumbers(Game game, Nation nation)
    {
        int unblockedFactories = CountUnblockedFactories(game, nation);
        int flagCount = game.TerritoryStates.Count(ts => ts.Controller == nation);
        int unitCount = game.Units.Count(u => u.Nation == nation);

        return (TaxationRules.ComputeRevenue(unblockedFactories, flagCount),
                TaxationRules.ComputeSoldiersPay(unitCount));
    }

    /// <summary>
    /// Factories of <paramref name="nation"/> that can actually be taxed: built in one of its own
    /// home provinces, with no hostile army standing there.
    /// </summary>
    public static int CountUnblockedFactories(Game game, Nation nation)
    {
        int count = 0;
        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (territoryDef == null || territoryDef.Nation != nation) continue;

            bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == ts.TerritoryId
                && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
            if (!hasHostileArmy) count++;
        }
        return count;
    }
}
