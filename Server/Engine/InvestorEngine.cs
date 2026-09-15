using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// The Investor turn (Imperial-2030-Rules.pdf p.11: interest, the 2M investor bonus, and the order in
/// which the card holder and the Swiss Banks invest) and the government rule (p.12: the highest credit
/// sum governs; a tie is not sufficient to displace the sitting government).
/// </summary>
public static class InvestorEngine
{
    public static void HandleInvestorPhase(ApplicationDbContext? context, Game game, NationState nationState, Player controller, bool isLandedOn)
    {
        var controllerName = controller.GetPlayerName(context);

        // 1. Paying out interest (ONLY if landed on)
        if (isLandedOn)
        {
            var bonds = game.Bonds.Where(b => b.Nation == nationState.Nation && b.HolderId != null).ToList();

            int owedToController = 0;
            int owedToOthers = 0;

            foreach (var bond in bonds)
            {
                if (bond.HolderId == controller.Id)
                    owedToController += bond.Interest;
                else
                    owedToOthers += bond.Interest;
            }

            // Pay Others First
            if (nationState.Treasury >= owedToOthers)
            {
                nationState.Treasury -= owedToOthers;
                // Distribute to others, one entry per HOLDER rather than per bond. Paying per bond made a
                // player holding two bonds in this nation show up as two consecutive "paid Nm interest to
                // X" lines while everyone else got one, which read as though they were being treated
                // differently — the controller's own payment below has always been logged as a single
                // combined total. Cash is identical either way; only the reporting changes.
                foreach (var holderBonds in bonds.Where(b => b.HolderId != controller.Id).GroupBy(b => b.HolderId))
                {
                    var holder = game.Players.First(p => p.Id == holderBonds.Key);
                    int owedToHolder = holderBonds.Sum(b => b.Interest);
                    holder.Cash += owedToHolder;
                    if (context != null) context.Entry(holder).State = EntityState.Modified;
                    var holderName = holder.GetPlayerName(context);
                    GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToHolder, holderName);
                }

                // Pay Controller
                if (nationState.Treasury >= owedToController && owedToController > 0)
                {
                    nationState.Treasury -= owedToController;
                    controller.Cash += owedToController;
                    GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToController, controllerName);
                }
                else if (nationState.Treasury > 0 && owedToController > 0)
                {
                    // Partial payment to controller
                    controller.Cash += nationState.Treasury;
                    GameLogger.LogInvestorInterestPartial(context, game, nationState.Nation, controllerName, nationState.Treasury, owedToController, controllerName);
                    nationState.Treasury = 0;
                }
                else if (owedToController > 0)
                {
                    GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, owedToController, controllerName, true, true);
                }
            }
            else
            {
                // Treasury insufficient for others
                int treasuryAmount = nationState.Treasury;
                nationState.Treasury = 0;

                // Calculate how much the controller can actually cover
                int deficit = owedToOthers - treasuryAmount;
                int paymentFromController = Math.Min(controller.Cash, deficit); // Cap at available cash

                controller.Cash -= paymentFromController;
                if (paymentFromController > 0)
                {
                    GameLogger.LogInvestorPersonallyContributed(context, game, nationState.Nation, controllerName, paymentFromController);
                }

                // Total funds available for others
                int totalForOthers = treasuryAmount + paymentFromController;

                // Distribute to others
                if (totalForOthers >= owedToOthers)
                {
                    // Full payment possible — grouped per holder for the same reason as the branch above.
                    foreach (var holderBonds in bonds.Where(b => b.HolderId != controller.Id).GroupBy(b => b.HolderId))
                    {
                        var holder = game.Players.First(p => p.Id == holderBonds.Key);
                        int owedToHolder = holderBonds.Sum(b => b.Interest);
                        holder.Cash += owedToHolder;
                        if (context != null) context.Entry(holder).State = EntityState.Modified;
                        var holderName = holder.GetPlayerName(context);
                        GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToHolder, holderName);
                    }
                }
                else
                {
                    // Partial payment (Lowest denomination first)
                    // Order bonds held by others by lowest interest (or lowest cost, they are correlated)
                    var otherBonds = bonds.Where(b => b.HolderId != controller.Id).OrderBy(b => b.Interest).ToList();
                    int remainingFunds = totalForOthers;
                    foreach (var bond in otherBonds)
                    {
                        if (remainingFunds >= bond.Interest)
                        {
                            var holder = game.Players.First(p => p.Id == bond.HolderId);
                            holder.Cash += bond.Interest;
                            if (context != null) context.Entry(holder).State = EntityState.Modified;
                            var holderName = holder.GetPlayerName(context);
                            GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, bond.Interest, holderName);
                            remainingFunds -= bond.Interest;
                        }
                        else
                        {
                            // Not enough to pay this bond fully. Give them the remaining funds as a partial payment.
                            if (remainingFunds > 0)
                            {
                                var holder = game.Players.First(p => p.Id == bond.HolderId);
                                holder.Cash += remainingFunds;
                                if (context != null) context.Entry(holder).State = EntityState.Modified;
                                var holderName = holder.GetPlayerName(context);
                                GameLogger.LogInvestorInterestPartial(context, game, nationState.Nation, controllerName, remainingFunds, bond.Interest, holderName);
                                remainingFunds = 0;
                            }
                            else
                            {
                                // No funds left at all
                                var holder = game.Players.First(p => p.Id == bond.HolderId);
                                var holderName = holder.GetPlayerName(context);
                                GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, bond.Interest, holderName, false, holderName == controllerName);
                            }
                        }
                    }

                    // Any leftover funds are returned to the treasury (should be 0 here because of the partial payment logic, 
                    // unless they somehow had EXACTLY enough to pay the first bond but not others, wait if they had exactly enough, remainingFunds is 0).
                    nationState.Treasury += remainingFunds;
                }

                if (owedToController > 0)
                {
                    GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, owedToController, controllerName, true, true);
                }
            }
        }

        // 2. Activating the Investor
        // 2M Bonus
        if (game.InvestorCardHolderId.HasValue)
        {
            var investor = game.Players.FirstOrDefault(p => p.Id == game.InvestorCardHolderId.Value);
            if (investor != null)
            {
                investor.Cash += 2;
                if (context != null) context.Entry(investor).State = EntityState.Modified;
                var investorName = investor.GetPlayerName(context);
                GameLogger.LogInvestorBonus(context, game, investorName, 2);
            }
        }

        // Determine investment order. Imperial-2030-Rules.pdf p.11 numbers these steps: "2. Activating the
        // Investor" - the card holder takes the 2M above and invests - and only then "3. Investing as Swiss
        // Bank". The card holder therefore picks FIRST; bonds are a scarce shared pool and the trade-in
        // mechanic makes first pick materially valuable, so this order changes who gets what.
        var eligibleInvestors = new List<Guid>();

        if (game.InvestorCardHolderId.HasValue)
        {
            eligibleInvestors.Add(game.InvestorCardHolderId.Value);
        }

        // Swiss Bank players = players who control 0 nations (p.12), taken in the order p.11 gives them:
        // "If several players have a Swiss Bank, investing is done in the order of play (clockwise),
        // starting from the player currently with the Investor card." So walk the play order rotated to
        // begin at the card holder rather than from its arbitrary head.
        var playOrder = game.Players.GetOrderedPlayers().Select(p => p.Id).ToList();
        int rotation = game.InvestorCardHolderId.HasValue ? playOrder.IndexOf(game.InvestorCardHolderId.Value) : -1;
        if (rotation < 0) rotation = 0; // no investor card in play: nothing to count from, keep play order

        var controlledNations = game.NationStates.Where(ns => ns.ControllerId.HasValue).Select(ns => ns.ControllerId).Distinct().ToList();

        var swissBankPlayers = Enumerable.Range(0, playOrder.Count)
            .Select(i => playOrder[(rotation + i) % playOrder.Count])
            .Where(id => !controlledNations.Contains(id))
            // A card holder who also holds a Swiss Bank does not get a second turn - FAQ p.14: "Can the
            // investor invest twice if he owns a Swiss Bank? No." They are already queued above.
            .Where(id => !eligibleInvestors.Contains(id))
            .ToList();

        eligibleInvestors.AddRange(swissBankPlayers);

        if (eligibleInvestors.Any())
        {
            game.IsInvestorTurn = true;
            game.ActingPlayerId = eligibleInvestors.First();
            game.PendingInvestorIds = eligibleInvestors.Skip(1).ToList();
        }
    }

    public static void UpdateNationController(ApplicationDbContext? context, Game game, Nation nation)
    {
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var bonds = game.Bonds.Where(b => b.Nation == nation && b.HolderId != null).ToList();

        if (!bonds.Any()) return; // No change if no bonds

        // Calculate total investment per player
        var investmentMap = new Dictionary<Guid, int>();
        foreach (var bond in bonds)
        {
            if (bond.HolderId.HasValue)
            {
                if (!investmentMap.ContainsKey(bond.HolderId.Value))
                    investmentMap[bond.HolderId.Value] = 0;

                investmentMap[bond.HolderId.Value] += bond.Cost;
            }
        }

        // Imperial-2030-Rules.pdf p.12: "If, due to the allocation of bonds, a new player has achieved the
        // highest credit sum (a tie is not sufficient), he takes over the government of that nation and is
        // given the nation flag card. If several players achieve the same highest credit sum, the player
        // first in seating order, counting from the player with the investor card, takes over the
        // government."
        //
        // So: an outright leader takes over, and a tie that includes the sitting government leaves it in
        // place ("a tie is not sufficient"). Note that "the player among them who bought a bond of the
        // nation most recently gets the card" - which an earlier comment here cited as a rule - does not
        // appear anywhere in the rulebook. Don't reason from it.

        if (!investmentMap.Any()) return;

        var currentControllerId = nationState.ControllerId;
        var topInvestor = investmentMap.OrderByDescending(kvp => kvp.Value).First();
        int maxInvestment = topInvestor.Value;

        var candidates = investmentMap.Where(kvp => kvp.Value == maxInvestment).Select(kvp => kvp.Key).ToList();

        if (candidates.Count == 1)
        {
            // Clear winner
            if (nationState.ControllerId != candidates[0])
            {
                nationState.ControllerId = candidates[0];
                if (context != null) context.Entry(nationState).State = EntityState.Modified;
            }
        }
        else
        {
            // Tie for the highest credit sum.
            if (currentControllerId.HasValue && candidates.Contains(currentControllerId.Value))
            {
                // The sitting government is among the tied leaders: "a tie is not sufficient" to displace
                // it, so it retains the nation flag card. Nothing to do.
            }
            else
            {
                // UNREACHABLE in real play, and left as a defensive fallback rather than built out.
                //
                // Reaching it needs the tied leaders to exclude the sitting government, and that cannot
                // happen: this method runs after each SINGLE bond purchase, so exactly one player's credit
                // sum changes per call, and the government always already holds the maximum (it is seeded
                // that way at setup - GameSetupHelper assigns it from the nation's 2M bond holder - and
                // every branch here preserves it). Let M be the old maximum, held by the government, and V
                // the buyer's new sum: V > M makes the buyer the sole candidate; V == M or V < M leaves the
                // government among the candidates and it retains. No path leaves it out.
                //
                // So the rulebook's own tie-break for this case - "the player first in seating order,
                // counting from the player with the investor card" (p.12) - has nothing to resolve here.
                // Do NOT implement it speculatively; if a future change can actually strand the government
                // off the maximum (e.g. bonds being returned mid-game), write the failing test first, then
                // replace this fallback with that rule.
                if (game.ActingPlayerId.HasValue && candidates.Contains(game.ActingPlayerId.Value))
                {
                    nationState.ControllerId = game.ActingPlayerId.Value;
                    if (context != null) context.Entry(nationState).State = EntityState.Modified;
                }
                else
                {
                    nationState.ControllerId = candidates[0];
                    if (context != null) context.Entry(nationState).State = EntityState.Modified;
                }
            }
        }
    }
}
