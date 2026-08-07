using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Helpers;

namespace Imperial2030.Tests
{
    public class ManeuverHelperTests
    {
        [Fact]
        public void TestArmyInControlledNeutralCannotUseRailToConvoy()
        {
            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.India,
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.India }
                },
                TerritoryStates = new List<TerritoryState>
                {
                    // India controls Afghanistan (neutral)
                    new TerritoryState { TerritoryId = "Afghanistan", Controller = Nation.India },
                    new TerritoryState { TerritoryId = "NewDelhi" },
                    new TerritoryState { TerritoryId = "Mumbai" },
                    new TerritoryState { TerritoryId = "Chennai" }
                },
                Units = new List<Unit>
                {
                    // Army in Afghanistan
                    new Unit { Nation = Nation.India, UnitType = UnitType.Army, TerritoryId = "Afghanistan", HasMoved = false },
                    // Fleet in Indian Ocean
                    new Unit { Nation = Nation.India, UnitType = UnitType.Fleet, TerritoryId = "IndianOcean", HasMoved = false }
                }
            };

            var destinations = ManeuverHelper.GetAllReachableArmyDestinations(game, "Afghanistan", Nation.India);
            var destIds = destinations.Select(d => d.TerritoryId).ToList();

            // Should be able to reach adjacent lands (e.g., NewDelhi, Mumbai, Iran, etc.) via regular land move.
            Assert.Contains("NewDelhi", destIds);
            
            // Should NOT be able to reach South Africa, because it would require a regular move into NewDelhi, rail to Mumbai, and convoy.
            // That combo is illegal.
            Assert.DoesNotContain("South-Africa", destIds);
        }
    }
}
