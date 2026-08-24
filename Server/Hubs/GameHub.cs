using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Imperial2030.Server.Data;
using Imperial2030.Server.Services;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Imperial2030.Server.Hubs;

public class GameHub : Hub
{
    private readonly PresenceTracker _presenceTracker;
    private readonly IServiceScopeFactory _scopeFactory;

    public GameHub(PresenceTracker presenceTracker, IServiceScopeFactory scopeFactory)
    {
        _presenceTracker = presenceTracker;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Whether <paramref name="gameId"/> names a real game.
    ///
    /// PresenceTracker keys off this string, so without the check any caller could grow a process-lifetime
    /// singleton without bound by joining invented ids. Parsing alone is not enough - fresh Guids are free
    /// to mint - so the row has to actually exist.
    /// </summary>
    private async Task<bool> IsRealGameAsync(string gameId)
    {
        if (!System.Guid.TryParse(gameId, out var parsed)) return false;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.Games.AsNoTracking().AnyAsync(g => g.Id == parsed);
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            bool wasOnline = _presenceTracker.IsUserOnline(userId);
            _presenceTracker.UserConnected(userId, Context.ConnectionId);

            if (!wasOnline)
            {
                await Clients.All.SendAsync("UserPresenceChanged", userId, true);
            }
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        var (observerUpdates, playerUpdates) = _presenceTracker.UserDisconnected(Context.ConnectionId);
        foreach (var update in observerUpdates)
        {
            await Clients.Group(update.Key).SendAsync("ObserverCountChanged", update.Value);
        }
        foreach (var update in playerUpdates)
        {
            foreach (var pUserId in update.Value)
            {
                bool stillActive = _presenceTracker.IsUserActiveInGame(update.Key, pUserId);
                await Clients.Group(update.Key).SendAsync("PlayerActiveChanged", pUserId, stillActive);
            }
        }

        if (!string.IsNullOrEmpty(userId) && !_presenceTracker.IsUserOnline(userId))
        {
            await Clients.All.SendAsync("UserPresenceChanged", userId, false);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<int> JoinGameGroup(string gameId, bool isObserver = false)
    {
        if (!await IsRealGameAsync(gameId)) return 0;

        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        int count = 0;
        if (!string.IsNullOrEmpty(userId))
        {
            if (isObserver)
            {
                count = _presenceTracker.AddObserver(gameId, userId, Context.ConnectionId);
                await Clients.Group(gameId).SendAsync("ObserverCountChanged", count);
            }
            else
            {
                _presenceTracker.AddActivePlayer(gameId, userId, Context.ConnectionId);
                count = _presenceTracker.GetObserverCount(gameId);
                await Clients.Group(gameId).SendAsync("PlayerActiveChanged", userId, true);
            }
        }
        return count;
    }

    public async Task LeaveGameGroup(string gameId, bool isObserver = false)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(userId))
        {
            if (isObserver)
            {
                int count = _presenceTracker.RemoveObserver(gameId, userId, Context.ConnectionId);
                await Clients.Group(gameId).SendAsync("ObserverCountChanged", count);
            }
            else
            {
                _presenceTracker.RemoveActivePlayer(gameId, userId, Context.ConnectionId);
                bool stillActive = _presenceTracker.IsUserActiveInGame(gameId, userId);
                await Clients.Group(gameId).SendAsync("PlayerActiveChanged", userId, stillActive);
            }
        }
    }
}
