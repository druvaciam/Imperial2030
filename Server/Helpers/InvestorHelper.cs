using Imperial2030.Server.Models;
using System.Linq;

namespace Imperial2030.Server.Helpers;

public static class InvestorHelper
{
    // Non-mutating simulation of GamesController.HandleInvestorPhase's payout logic (the isLandedOn branch),
    // for the current controller of `nationState` right now. Mirrors TaxationHelper.PreviewTaxation.
    //
    // NetControllerCashDelta: how the controller's cash would change if this nation landed on Investor this
    // instant — positive when they'd receive their own interest (in full or partial), negative when treasury
    // can't cover what's owed to OTHER bondholders and the controller has to personally cover the shortfall
    // from their own pocket (capped at what cash they actually have).
    //
    // WillGetFullOwnInterest: true only when the controller would receive every M of interest they're
    // owed on their own bonds in this nation (or trivially true if they hold none). False covers partial
    // payment, being unable to pay them at all, and the personal-contribution branch (where the controller
    // never receives their own interest at all, regardless of how much they end up paying out of pocket).
    public static (int NetControllerCashDelta, bool WillGetFullOwnInterest) PreviewInterestPayment(Game game, NationState nationState, Player controller)
    {
        var bonds = game.Bonds.Where(b => b.Nation == nationState.Nation && b.HolderId != null).ToList();

        int owedToController = bonds.Where(b => b.HolderId == controller.Id).Sum(b => b.Interest);
        int owedToOthers = bonds.Where(b => b.HolderId != controller.Id).Sum(b => b.Interest);

        if (nationState.Treasury >= owedToOthers)
        {
            int remaining = nationState.Treasury - owedToOthers;
            if (remaining >= owedToController && owedToController > 0)
            {
                return (owedToController, true); // Full payment
            }
            else if (remaining > 0 && owedToController > 0)
            {
                return (remaining, false); // Partial payment
            }
            else
            {
                return (0, owedToController == 0); // Nothing owed, or unable to pay any of it
            }
        }
        else
        {
            // Treasury can't even cover what's owed to others; controller never receives their own interest
            // here, and instead may have to personally cover part of the shortfall for others.
            int deficit = owedToOthers - nationState.Treasury;
            int paymentFromController = System.Math.Min(controller.Cash, deficit);
            return (-paymentFromController, owedToController == 0);
        }
    }
}
