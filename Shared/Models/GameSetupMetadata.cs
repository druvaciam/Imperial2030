using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models
{
    public class GameSetupMetadata
    {
        // Maps each dealt package's primary Nation to the Player who received it. Everything else StartGame
        // computes (secondary bonds, treasury credits, controllers, investor card holder, starting cash) is a
        // deterministic function of this plus the fixed package table and player count, so this is the one
        // piece of StartGame's randomness that must be captured for a game to be reproducible from its action
        // log alone.
        public Dictionary<Nation, Guid> NationDistribution { get; set; } = new();

        // Snapshot of the final lobby roster at the moment StartGame fires. Lobby churn (join/leave/add-bot/
        // remove-bot) before this point never needs to be individually reconstructed - only the roster StartGame
        // actually used matters for replay/import, and it's captured once here rather than event-sourced.
        public List<PlayerRosterEntry> Players { get; set; } = new();

        public int MaxPlayers { get; set; }
        public bool IsPrivate { get; set; }
        public bool VariantBonusOnlyForTaxIncreases { get; set; }
    }

    public class PlayerRosterEntry
    {
        public Guid PlayerId { get; set; }
        public string? UserId { get; set; }
        public bool IsHost { get; set; }
        public bool IsBot { get; set; }
        public string? BotName { get; set; }
        public string? BotType { get; set; }

        // Resolved once at StartGame time so it stays stable even if the source account is later renamed/deleted.
        public string? DisplayName { get; set; }
    }
}
