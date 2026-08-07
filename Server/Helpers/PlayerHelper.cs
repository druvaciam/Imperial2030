using Imperial2030.Server.Models;
using Imperial2030.Server.Data;
using System.Linq;

namespace Imperial2030.Server.Helpers;

public static class PlayerHelper
{
    public static string GetPlayerName(this Player p, ApplicationDbContext? context = null)
    {
        if (p == null) return "Unknown";
        if (p.IsBot) return p.BotName ?? "Bot";
        if (p.User != null) return p.User.UserName ?? "Player";
        if (context != null)
        {
            var user = context.Users.FirstOrDefault(u => u.Id == p.UserId);
            return user?.UserName ?? p.UserId ?? "Player";
        }
        return p.UserId ?? "Player";
    }
}