using System;
using Imperial2030.Server.Services;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// PresenceTracker is a singleton that decremented its counters to zero but never removed the entries,
    /// so every user who ever connected and every game anyone ever opened stayed in its dictionaries for
    /// the lifetime of the process. The public presence API filters zero-valued entries out, which is why
    /// the leak was invisible - these assert on the tracker's own diagnostic counts instead.
    /// </summary>
    public class PresenceTrackerTests
    {
        private const string GameId = "0f1e4c7a-2b3d-4e5f-8a9b-0c1d2e3f4a5b";

        [Fact]
        public void UserDisconnected_RemovesTheUsersEntryRatherThanLeavingItAtZero()
        {
            var tracker = new PresenceTracker();
            tracker.UserConnected("user-1", "conn-1");
            Assert.Equal(1, tracker.TrackedUserCount);

            tracker.UserDisconnected("conn-1");

            Assert.False(tracker.IsUserOnline("user-1"));
            Assert.Equal(0, tracker.TrackedUserCount);
        }

        [Fact]
        public void UserDisconnected_WithAnotherConnectionStillOpen_KeepsTheUserOnline()
        {
            // The counter is per connection, so dropping one of two tabs must not evict the user.
            var tracker = new PresenceTracker();
            tracker.UserConnected("user-1", "conn-1");
            tracker.UserConnected("user-1", "conn-2");

            tracker.UserDisconnected("conn-1");

            Assert.True(tracker.IsUserOnline("user-1"));
            Assert.Equal(1, tracker.TrackedUserCount);

            tracker.UserDisconnected("conn-2");

            Assert.False(tracker.IsUserOnline("user-1"));
            Assert.Equal(0, tracker.TrackedUserCount);
        }

        [Fact]
        public void RemoveObserver_WhenTheLastObserverLeaves_DropsTheGameEntry()
        {
            var tracker = new PresenceTracker();
            tracker.UserConnected("user-1", "conn-1");
            Assert.Equal(1, tracker.AddObserver(GameId, "user-1", "conn-1"));
            Assert.Equal(1, tracker.TrackedObserverGameCount);

            Assert.Equal(0, tracker.RemoveObserver(GameId, "user-1", "conn-1"));

            Assert.Equal(0, tracker.GetObserverCount(GameId));
            Assert.Equal(0, tracker.TrackedObserverGameCount);
        }

        [Fact]
        public void RemoveObserver_WithAnotherObserverStillWatching_KeepsTheGameEntry()
        {
            var tracker = new PresenceTracker();
            tracker.AddObserver(GameId, "user-1", "conn-1");
            tracker.AddObserver(GameId, "user-2", "conn-2");

            Assert.Equal(1, tracker.RemoveObserver(GameId, "user-1", "conn-1"));

            Assert.Equal(1, tracker.GetObserverCount(GameId));
            Assert.Equal(1, tracker.TrackedObserverGameCount);
        }

        [Fact]
        public void RemoveActivePlayer_WhenTheLastPlayerLeaves_DropsTheGameEntry()
        {
            var tracker = new PresenceTracker();
            tracker.AddActivePlayer(GameId, "user-1", "conn-1");
            Assert.True(tracker.IsUserActiveInGame(GameId, "user-1"));
            Assert.Equal(1, tracker.TrackedActivePlayerGameCount);

            tracker.RemoveActivePlayer(GameId, "user-1", "conn-1");

            Assert.False(tracker.IsUserActiveInGame(GameId, "user-1"));
            Assert.Equal(0, tracker.TrackedActivePlayerGameCount);
        }

        [Fact]
        public void UserDisconnected_DropsTheGamesThatConnectionWasTheLastPresenceIn()
        {
            // The disconnect path decrements through its own code, separate from Remove*; it leaked too.
            var tracker = new PresenceTracker();
            tracker.UserConnected("user-1", "conn-1");
            tracker.AddObserver(GameId, "user-1", "conn-1");
            tracker.AddActivePlayer(GameId, "user-1", "conn-1");

            tracker.UserDisconnected("conn-1");

            Assert.Equal(0, tracker.TrackedUserCount);
            Assert.Equal(0, tracker.TrackedObserverGameCount);
            Assert.Equal(0, tracker.TrackedActivePlayerGameCount);
        }

        [Fact]
        public void RemoveGame_ForgetsAGameEntirely()
        {
            // Called when a game is deleted: nobody has disconnected, so the per-connection paths above
            // would never clean these up.
            var tracker = new PresenceTracker();
            tracker.UserConnected("user-1", "conn-1");
            tracker.AddObserver(GameId, "user-1", "conn-1");
            tracker.AddActivePlayer(GameId, "user-2", "conn-2");

            tracker.RemoveGame(GameId);

            Assert.Equal(0, tracker.TrackedObserverGameCount);
            Assert.Equal(0, tracker.TrackedActivePlayerGameCount);
            Assert.Equal(0, tracker.GetObserverCount(GameId));
            Assert.False(tracker.IsUserActiveInGame(GameId, "user-2"));
            // The users themselves are still connected and must be untouched.
            Assert.True(tracker.IsUserOnline("user-1"));
        }
    }
}
