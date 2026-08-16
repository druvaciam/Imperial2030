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
    }
}
