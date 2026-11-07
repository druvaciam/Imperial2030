using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers;

/// <summary>
/// What a rondel space would be worth to a nation from the position as it stands, without moving
/// anything - the same kind of non-mutating preview as <see cref="TaxationHelper.PreviewTaxation"/> and
/// <see cref="InvestorHelper.PreviewInterestPayment"/>, which it uses.
/// </summary>
public static class RondelOutlookHelper
{
    /// <summary>
    /// Whether landing on <paramref name="targetSlot"/> would waste the turn: the space's action cannot
    /// do anything for this nation from the position as it stands. Lifted verbatim out of the state
    /// vector's per-move preview loop, its first caller, so the flag the policy is
    /// trained on and anything else that asks the question cannot drift apart.
    /// </summary>
    public static bool IsWastedRondelSpace(Game game, NationState actingNs, int targetSlot)
    {
        bool isPenalized = false;

        if (targetSlot == RondelData.FactorySlot)
        {
            bool noMoney = actingNs.Treasury < 5;
            bool allBuiltOrBlocked = false;
            if (!noMoney)
            {
                var homeCities = TerritoryData.AllTerritories
                    .Where(t => t.Nation == actingNs.Nation && t.CityType != CityType.None);
                allBuiltOrBlocked = homeCities.All(city =>
                {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                    if (ts != null && ts.HasFactory) return true;
                    return game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != actingNs.Nation && u.IsHostile);
                });
            }
            if (noMoney || allBuiltOrBlocked) isPenalized = true;
        }
        else if (targetSlot == RondelData.ImportSlot)
        {
            if (actingNs.Treasury < 1) isPenalized = true;
        }
        else if (RondelData.IsManeuverSlot(targetSlot))
        {
            bool hasUnits = game.Units.Any(u => u.Nation == actingNs.Nation);
            if (!hasUnits) isPenalized = true;
        }
        else if (RondelData.IsProductionSlot(targetSlot))
        {
            // Wasted Production: no unblockaded home factory can currently produce (every eligible
            // factory's unit type is already at cap). This branch was missing entirely - every other
            // actionable slot in this loop previews its own "wasted move" reward penalty, but
            // Production fell through to isPenalized=false unconditionally, so the agent had zero
            // signal before a choice the reward function (RondelData.IsProductionSlot branch further
            // down this file) penalizes -10 for. Same class of bug as the Investor gap above, found by
            // the same systematic check: does every reward penalty have a matching preview flag here.
            var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == actingNs.Nation);
            int currentArmies = game.Units.Count(u => u.Nation == actingNs.Nation && u.UnitType == UnitType.Army);
            int currentFleets = game.Units.Count(u => u.Nation == actingNs.Nation && u.UnitType == UnitType.Fleet);
            int maxArmies = NationData.GetMaxArmies(actingNs.Nation);
            int maxFleets = NationData.GetMaxFleets(actingNs.Nation);

            bool canProduceAnything = homeCities.Any(city =>
            {
                var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                if (ts == null || !ts.HasFactory) return false;
                bool isBlockaded = game.Units.Any(u => u.TerritoryId == city.Id && u.Nation != actingNs.Nation && u.UnitType == UnitType.Army && u.IsHostile);
                if (isBlockaded) return false;
                return city.CityType == CityType.LightBlue ? currentFleets < maxFleets : currentArmies < maxArmies;
            });

            if (!canProduceAnything) isPenalized = true;
        }
        else if (targetSlot == RondelData.InvestorSlot)
        {
            // Landing on Investor pays interest on the acting nation's own bonds from its own treasury
            // (others first, then the controller); it's "penalized" whenever the controller nets <= 0
            // out of it — either paying a shortfall from their own pocket, or receiving nothing at all.
            // Reuses InvestorHelper.PreviewInterestPayment, the same non-mutating preview already used
            // for the PersonalContribution/MissedInterest reward penalties and the raw preview floats
            // appended later in this vector — this is not new game logic, just an added consumer of it.
            var moveController = game.Players.FirstOrDefault(p => p.Id == actingNs.ControllerId);
            if (moveController != null)
            {
                var investorMovePreview = InvestorHelper.PreviewInterestPayment(game, actingNs, moveController);
                if (investorMovePreview.NetControllerCashDelta <= 0) isPenalized = true;
            }
        }
        else if (targetSlot == RondelData.TaxationSlot)
        {
            // Wasted Taxation: no power-track gain AND no treasury gain. Mirrors the reward penalty
            // for a fully wasted Taxation turn exactly ("expectedTaxTreasuryGain <= 0 &&
            // expectedTaxPowerGain == 0") by reading the SAME already-computed ExpectedTreasuryGain
            // field PreviewTaxation returns, rather than re-deriving an approximation of it from
            // TotalTaxRevenue - SoldiersPay. That approximation was wrong in one real case: when
            // treasury is low enough that actual soldiers' pay gets capped below the raw SoldiersPay
            // figure (TaxationHelper caps it at what's actually payable), the approximation could
            // read "wasted" while the real ExpectedTreasuryGain (which uses the capped payment) was
            // still positive - a false-positive flag telling the agent to avoid a Taxation visit that
            // the reward function would not actually have penalized.
            var taxMovePreview = TaxationHelper.PreviewTaxation(game, actingNs);
            if (taxMovePreview.ExpectedPowerGain == 0 && taxMovePreview.ExpectedTreasuryGain <= 0) isPenalized = true;
        }

        return isPenalized;
    }

    /// <summary>
    /// Whether this rondel move walked the marker past Taxation while stopping there would have paid
    /// <see cref="SkippedTaxationPowerThreshold"/> or more power. The passed space is nearer than the
    /// one the move paid to reach, so stopping on it was affordable by definition (p.6: the first three
    /// spaces are free, each further space costs more) - no cash check is needed.
    ///
    /// Power is the win condition (p.6: 25 points ends the game), and nothing in the reward compared a
    /// move against the taxation it gave up: measured over five games each, RL-5 walked past a 5+ power
    /// Taxation on 25 of 62 chances, RL-4 on 26 of 71, the heuristic Default bot on 15 of 60.
    /// </summary>
    public static bool SkippedValuableTaxation(int fromSlot, int targetSlot, int expectedTaxPowerGain)
    {
        if (expectedTaxPowerGain < SkippedTaxationPowerThreshold) return false;

        int toTaxation = RondelData.GetMoveDistance(fromSlot, RondelData.TaxationSlot);
        if (toTaxation == 0) return false; // Leaving Taxation is not passing it.

        return toTaxation < RondelData.GetMoveDistance(fromSlot, targetSlot);
    }

    /// <summary>
    /// Whether this move paid to reach the farther of the rondel's two identical spaces - Production is
    /// on spaces 2 and 6, Maneuver on 3 and 7 (p.6) - when the nearer one was reachable and cheaper. The
    /// action is the same either way, so the difference in move cost is money spent for nothing.
    ///
    /// Checked for both of each pair. It used to be checked only for the FIRST of each pair, so the move
    /// that cost this game 3M - Taxation to Production 2 (six spaces) with Production 1 two spaces away
    /// and free - went unpenalised.
    /// </summary>
    public static bool PaidForTheFartherTwinSlot(int fromSlot, int targetSlot, int power)
    {
        int twin = targetSlot switch
        {
            RondelData.ProductionSlot1 => RondelData.ProductionSlot2,
            RondelData.ProductionSlot2 => RondelData.ProductionSlot1,
            RondelData.ManeuverSlot1 => RondelData.ManeuverSlot2,
            RondelData.ManeuverSlot2 => RondelData.ManeuverSlot1,
            _ => -1
        };
        if (twin < 0) return false;

        int toTwin = RondelData.GetMoveDistance(fromSlot, twin);
        // The marker is standing on the twin, and staying is not a move (p.6) - there was no cheaper option.
        if (toTwin == 0) return false;
        if (toTwin >= RondelData.GetMoveDistance(fromSlot, targetSlot)) return false;

        return RondelData.GetMoveCost(fromSlot, targetSlot, power) > RondelData.GetMoveCost(fromSlot, twin, power);
    }

    /// <summary>The power gain that makes a Taxation worth stopping for. Mirrors the +5 already paid for taxing above it.</summary>
    public const int SkippedTaxationPowerThreshold = 5;


    /// <summary>
    /// Whether the move a bot has settled on is one this code can already see is questionable - the only
    /// kind of move a second opinion can reliably improve. Measured over 84 head-to-head games, taking a
    /// second opinion on every rondel move was a coin flip; these are the cases where it is not.
    ///
    /// Questionable means any of: the space's action can do nothing from here; the marker walks past a
    /// Taxation worth <see cref="SkippedTaxationPowerThreshold"/> or more power; the move pays extra to
    /// reach the farther of two spaces carrying the same action; or the nation could cross into the next
    /// power factor by taxing this turn - the only way power turns into score (p.6, read in fives) - and
    /// the move goes somewhere else instead.
    /// </summary>
    /// <param name="offeredSlots">The spaces the nation may legally move to, Taxation among them or not.</param>
    public static bool MoveLooksQuestionable(Game game, NationState actingNs, int chosenSlot, IEnumerable<int> offeredSlots)
    {
        if (!actingNs.RondelPosition.HasValue) return false;
        int from = actingNs.RondelPosition.Value;

        if (IsWastedRondelSpace(game, actingNs, chosenSlot)) return true;
        if (PaidForTheFartherTwinSlot(from, chosenSlot, actingNs.Power)) return true;

        var taxation = TaxationHelper.PreviewTaxation(game, actingNs);
        if (SkippedValuableTaxation(from, chosenSlot, taxation.ExpectedPowerGain)) return true;

        if (chosenSlot != RondelData.TaxationSlot && offeredSlots.Contains(RondelData.TaxationSlot))
        {
            int projected = Math.Min(GameConstants.MaxPowerPoints, actingNs.Power + taxation.ExpectedPowerGain);
            if (RondelData.GetPowerFactor(projected) > RondelData.GetPowerFactor(actingNs.Power)) return true;
        }

        return false;
    }

    /// <summary>
    /// How many units a Production would actually build for this nation: one per unblocked home factory,
    /// capped by the pieces the nation still has off the board. Lifted out of BotStrategyBase, which was
    /// its only caller until the Jev layer needed to say the same number in plain words.
    /// </summary>
    public static int EstimateProductionYield(Game game, Nation nation)
    {
        int produced = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation != nation) continue;
            bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
            if (blocked) continue;

            var unitType = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;
            if (unitType == UnitType.Army)
            {
                if (currentArmies >= NationData.GetMaxArmies(nation)) continue;
                currentArmies++;
            }
            else
            {
                if (currentFleets >= NationData.GetMaxFleets(nation)) continue;
                currentFleets++;
            }
            produced++;
        }
        return produced;
    }
}
