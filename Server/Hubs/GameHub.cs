using Microsoft.AspNetCore.SignalR;
using Imperial2030.Server.Services;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Imperial2030.Server.Hubs;

public class GameHub : Hub
{
    private readonly PresenceTracker _presenceTracker;

    public GameHub(PresenceTracker presenceTracker)
    {
        _presenceTracker = presenceTracker;
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
        
        _presenceTracker.UserDisconnected(Context.ConnectionId);

        if (!string.IsNullOrEmpty(userId) && !_presenceTracker.IsUserOnline(userId))
        {
            await Clients.All.SendAsync("UserPresenceChanged", userId, false);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinGameGroup(string gameId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
    }

    public async Task LeaveGameGroup(string gameId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
    }
}
