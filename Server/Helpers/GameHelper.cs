using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace Imperial2030.Server.Helpers;

public static class GameHelper
{
    public static async Task SetWinnerNameAsync(this Game game, ApplicationDbContext context)
    {
        if (game == null || context == null) return;

        await context.Entry(game).Collection(g => g.Players).LoadAsync();
        await context.Entry(game).Collection(g => g.NationStates).LoadAsync();
        await context.Entry(game).Collection(g => g.Bonds).LoadAsync();

        var winnerId = game.GetRankedPlayers().FirstOrDefault()?.Id;
        if (winnerId.HasValue)
        {
            var winnerPlayer = game.Players.FirstOrDefault(p => p.Id == winnerId.Value);
            if (winnerPlayer != null)
            {
                game.WinnerName = winnerPlayer.GetPlayerName(context);
            }
        }
    }
}
