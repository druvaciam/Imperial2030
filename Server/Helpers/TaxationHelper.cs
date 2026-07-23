using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using System;
using System.Linq;

namespace Imperial2030.Server.Helpers;

public static class TaxationHelper
{
    public static (int TotalTaxRevenue, int SoldiersPay, int Bonus, int PowerGain) ApplyTaxation(Game game, NationState nationState, Player controller)
    {
        var nation = nationState.Nation;
        int factoryRevenue = 0;
        var territoriesWithFactories = game.TerritoryStates.Where(ts => ts.HasFactory).ToList();
        
        foreach (var ts in territoriesWithFactories)
        {
            var territoryDef = Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (territoryDef != null && territoryDef.Nation == nation) 
            {
                bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
                if (!hasHostileArmy)
                {
                    factoryRevenue += 2;
                }
            }
        }

        int flagRevenue = game.TerritoryStates.Count(ts => ts.Controller == nation);
        int totalTaxRevenue = Math.Min(23, factoryRevenue + flagRevenue);

        nationState.Treasury += totalTaxRevenue;

        int unitCount = game.Units.Count(u => u.Nation == nation);
        int soldiersPay = unitCount * 1;

        if (nationState.Treasury >= soldiersPay)
        {
            nationState.Treasury -= soldiersPay;
        }
        else
        {
            nationState.Treasury = 0;
        }

        int bonus = 0;
        if (game.VariantBonusOnlyForTaxIncreases)
        {
            int oldTier = Imperial2030.Shared.Constants.TaxChart.GetPowerGain(nationState.TaxRevenue);
            int newTier = Imperial2030.Shared.Constants.TaxChart.GetPowerGain(totalTaxRevenue);
            bonus = Math.Max(0, newTier - oldTier);
        }
        else
        {
            bonus = Imperial2030.Shared.Constants.TaxChart.GetStandardBonus(totalTaxRevenue);
        }
        
        if (nationState.Treasury < bonus)
        {
            bonus = nationState.Treasury;
        }
        
        if (bonus > 0)
        {
            nationState.Treasury -= bonus;
            controller.Cash += bonus;
        }

        int powerGain = Imperial2030.Shared.Constants.TaxChart.GetPowerGain(totalTaxRevenue);
        nationState.Power += powerGain;
        if (nationState.Power > 25) nationState.Power = 25;

        nationState.PreviousTaxRevenue = nationState.TaxRevenue;
        nationState.TaxRevenue = totalTaxRevenue;

        return (totalTaxRevenue, soldiersPay, bonus, powerGain);
    }
}
