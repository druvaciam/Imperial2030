using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Imperial2030.Server.Services;

public class PresenceTracker
{
    private readonly ConcurrentDictionary<string, string> _connectionUsers = new();
    private readonly ConcurrentDictionary<string, int> _userConnections = new();

    // gameId -> (userId -> connection count)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _gameObservers = new();
    // connectionId -> list of gameIds observed
    private readonly ConcurrentDictionary<string, HashSet<string>> _connectionObservedGames = new();

    // gameId -> (userId -> connection count)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _gamePlayers = new();
    // connectionId -> list of gameIds played
    private readonly ConcurrentDictionary<string, HashSet<string>> _connectionPlayedGames = new();

    public void UserConnected(string userId, string connectionId)
    {
        _connectionUsers[connectionId] = userId;
        _userConnections.AddOrUpdate(userId, 1, (_, count) => count + 1);
    }

    public (Dictionary<string, int> ObserverUpdates, Dictionary<string, List<string>> PlayerUpdates) UserDisconnected(string connectionId)
    {
        var observerUpdates = new Dictionary<string, int>();
        var playerUpdates = new Dictionary<string, List<string>>(); // gameId -> list of userIds that disconnected play status
        if (_connectionUsers.TryRemove(connectionId, out var userId))
        {
            DecrementOrRemove(_userConnections, userId);

            if (_connectionObservedGames.TryRemove(connectionId, out var games))
            {
                HashSet<string> gamesToUpdate;
                lock (games) { gamesToUpdate = new HashSet<string>(games); }

                foreach (var gameId in gamesToUpdate)
                {
                    if (_gameObservers.TryGetValue(gameId, out var observers))
                    {
                        DecrementOrRemove(observers, userId);
                        observerUpdates[gameId] = observers.Count(kvp => kvp.Value > 0);
                        DropIfEmpty(_gameObservers, gameId, observers);
                    }
                }
            }
            if (_connectionPlayedGames.TryRemove(connectionId, out var playedGames))
            {
                HashSet<string> gamesToUpdate;
                lock (playedGames) { gamesToUpdate = new HashSet<string>(playedGames); }

                foreach (var gameId in gamesToUpdate)
                {
                    if (_gamePlayers.TryGetValue(gameId, out var players))
                    {
                        DecrementOrRemove(players, userId);

                        if (!playerUpdates.ContainsKey(gameId)) playerUpdates[gameId] = new List<string>();
                        playerUpdates[gameId].Add(userId);

                        DropIfEmpty(_gamePlayers, gameId, players);
                    }
                }
            }
        }
        return (observerUpdates, playerUpdates);
    }

    public int AddObserver(string gameId, string userId, string connectionId)
    {
        _connectionObservedGames.AddOrUpdate(connectionId,
            _ => new HashSet<string> { gameId },
            (_, set) => { lock (set) { set.Add(gameId); } return set; });

        var observers = _gameObservers.GetOrAdd(gameId, _ => new ConcurrentDictionary<string, int>());
        observers.AddOrUpdate(userId, 1, (_, count) => count + 1);

        return observers.Count(kvp => kvp.Value > 0);
    }

    public int RemoveObserver(string gameId, string userId, string connectionId)
    {
        if (_connectionObservedGames.TryGetValue(connectionId, out var set))
        {
            lock (set) { set.Remove(gameId); }
        }

        if (_gameObservers.TryGetValue(gameId, out var observers))
        {
            DecrementOrRemove(observers, userId);
            int remaining = observers.Count(kvp => kvp.Value > 0);
            DropIfEmpty(_gameObservers, gameId, observers);
            return remaining;
        }
        return 0;
    }

    public int GetObserverCount(string gameId)
    {
        if (_gameObservers.TryGetValue(gameId, out var observers))
        {
            return observers.Count(kvp => kvp.Value > 0);
        }
        return 0;
    }

    public void AddActivePlayer(string gameId, string userId, string connectionId)
    {
        _connectionPlayedGames.AddOrUpdate(connectionId,
            _ => new HashSet<string> { gameId },
            (_, set) => { lock (set) { set.Add(gameId); } return set; });

        var players = _gamePlayers.GetOrAdd(gameId, _ => new ConcurrentDictionary<string, int>());
        players.AddOrUpdate(userId, 1, (_, count) => count + 1);
    }

    public void RemoveActivePlayer(string gameId, string userId, string connectionId)
    {
        if (_connectionPlayedGames.TryGetValue(connectionId, out var set))
        {
            lock (set) { set.Remove(gameId); }
        }

        if (_gamePlayers.TryGetValue(gameId, out var players))
        {
            DecrementOrRemove(players, userId);
            DropIfEmpty(_gamePlayers, gameId, players);
        }
    }

    public bool IsUserActiveInGame(string gameId, string userId)
    {
        if (_gamePlayers.TryGetValue(gameId, out var players))
        {
            return players.TryGetValue(userId, out var count) && count > 0;
        }
        return false;
    }

    public bool IsUserOnline(string userId)
    {
        return _userConnections.TryGetValue(userId, out var count) && count > 0;
    }

    public IEnumerable<string> GetOnlineUsers()
    {
        return _userConnections.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key);
    }

    /// <summary>
    /// Forgets a game outright. Called when a game is deleted: nobody has disconnected, so the
    /// per-connection cleanup paths above would never reach these entries.
    /// </summary>
    public void RemoveGame(string gameId)
    {
        _gameObservers.TryRemove(gameId, out _);
        _gamePlayers.TryRemove(gameId, out _);
    }

    // --- Diagnostics -------------------------------------------------------------------------------
    // The presence API filters zero-valued entries out, so a leaked entry is invisible through it. These
    // expose the raw dictionary sizes so the pruning above is actually assertable.

    public int TrackedUserCount => _userConnections.Count;
    public int TrackedObserverGameCount => _gameObservers.Count;
    public int TrackedActivePlayerGameCount => _gamePlayers.Count;

    // --- Pruning helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Decrements a per-key connection count, removing the key once it reaches zero instead of leaving a
    /// permanent 0 behind.
    /// </summary>
    private static void DecrementOrRemove(ConcurrentDictionary<string, int> counts, string key)
    {
        int updated = counts.AddOrUpdate(key, 0, (_, count) => System.Math.Max(0, count - 1));
        if (updated != 0) return;

        // Value-matched removal: a reconnect that raced with this will have bumped the count above zero,
        // and this call then leaves it alone rather than dropping a live entry.
        counts.TryRemove(new KeyValuePair<string, int>(key, 0));
    }

    /// <summary>
    /// Drops a game's per-user dictionary once nobody is left in it.
    /// </summary>
    /// <remarks>
    /// Reference-matched removal, then re-added if a concurrent Add* populated the very instance being
    /// removed. Presence is advisory UI state, so the remaining window is a count that corrects itself on
    /// the next join or disconnect rather than anything the game rules depend on.
    /// </remarks>
    private static void DropIfEmpty(ConcurrentDictionary<string, ConcurrentDictionary<string, int>> games,
                                    string gameId,
                                    ConcurrentDictionary<string, int> entries)
    {
        if (!entries.IsEmpty) return;

        if (games.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, int>>(gameId, entries))
            && !entries.IsEmpty)
        {
            games.TryAdd(gameId, entries);
        }
    }
}
