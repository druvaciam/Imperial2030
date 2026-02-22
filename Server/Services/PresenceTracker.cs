using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Imperial2030.Server.Services;

public class PresenceTracker
{
    private readonly ConcurrentDictionary<string, string> _connectionUsers = new();
    private readonly ConcurrentDictionary<string, int> _userConnections = new();

    public void UserConnected(string userId, string connectionId)
    {
        _connectionUsers[connectionId] = userId;
        _userConnections.AddOrUpdate(userId, 1, (_, count) => count + 1);
    }

    public void UserDisconnected(string connectionId)
    {
        if (_connectionUsers.TryRemove(connectionId, out var userId))
        {
            _userConnections.AddOrUpdate(userId, 0, (_, count) => System.Math.Max(0, count - 1));
        }
    }

    public bool IsUserOnline(string userId)
    {
        return _userConnections.TryGetValue(userId, out var count) && count > 0;
    }

    public IEnumerable<string> GetOnlineUsers()
    {
        return _userConnections.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key);
    }
}
