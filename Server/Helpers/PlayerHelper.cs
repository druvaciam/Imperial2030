using Imperial2030.Server.Models;
using Imperial2030.Server.Data;
using System.Linq;

namespace Imperial2030.Server.Helpers;

public static class PlayerHelper
{
    public static string GetPlayerName(this Player p, ApplicationDbContext? context = null)
    {
        if (p == null) return "Unknown";
        // Checked before IsBot rather than gated behind it: GameReplayService deliberately keeps every
        // imported/replay-session player's IsBot false for the DURATION of a replay (so BotService never
        // fires a concurrent, conflicting move against the same game row), while its BotName is already set
        // to the player's real display name. If this method gated on the live IsBot flag, every action
        // logged mid-replay (which all resolve their PlayerName through this method) would use a DIFFERENT
        // identity — the throwaway placeholder ApplicationUser's UserName, via the User-lookup branch below —
        // than the one recorded in that same game's own StartGame roster snapshot, breaking that game from
        // ever being replayed a second time (its own action log's PlayerNames wouldn't agree with each
        // other). Safe for existing real gameplay: a genuinely human Player's BotName is never populated by
        // any of CreateGame/JoinGame's real, production player-creation code paths.
        if (!string.IsNullOrEmpty(p.BotName)) return p.BotName;
        if (p.IsBot) return "Bot";
        if (p.User != null) return p.User.UserName ?? "Player";
        if (context != null)
        {
            var user = context.Users.FirstOrDefault(u => u.Id == p.UserId);
            return user?.UserName ?? p.UserId ?? "Player";
        }
        return p.UserId ?? "Player";
    }

    public static IOrderedEnumerable<Player> GetOrderedPlayers(this IEnumerable<Player> players)
    {
        return players.OrderBy(p => p.Id);
    }

    public static Guid GetNextPlayerId(Game game, Guid currentId)
    {
        var sortedParams = game.Players.GetOrderedPlayers().ToList();
        var index = sortedParams.FindIndex(p => p.Id == currentId);
        if (index == -1) return currentId; // Fallback
        var nextIndex = (index + 1) % sortedParams.Count;
        return sortedParams[nextIndex].Id;
    }
}