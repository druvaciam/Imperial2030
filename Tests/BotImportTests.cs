using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests
{
    public class BotImportTests
    {
        [Fact]
        public void NonRlBots_ChooseImports_NeverPlacesArmyInLondon()
        {
            var strategy = new DefaultBotStrategy();
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Units = new List<Unit>()
            };

            var ns = new NationState
            {
                Nation = Nation.Europe,
                Treasury = 10
            };

            var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == Nation.Europe).ToList();

            // Run import multiple rounds
            for (int i = 0; i < 5; i++)
            {
                var imports = strategy.ChooseImports(game, ns, maxImport: 3, homeTerritories);

                // Assert that NO imported unit placed in London is an Army
                var londonArmy = imports.FirstOrDefault(x => x.TerritoryId.Equals("London", StringComparison.OrdinalIgnoreCase) && x.Type == UnitType.Army);
                Assert.True(londonArmy == default, "Non-RL bot placed an Army in London during Import!");

                foreach (var imp in imports)
                {
                    game.Units.Add(new Unit
                    {
                        Nation = Nation.Europe,
                        TerritoryId = imp.TerritoryId,
                        UnitType = imp.Type
                    });
                }
            }
        }

        [Fact]
        public void NonRlBots_ChooseImports_WhenOnlyLondonAvailable_ImportsFleetNotArmy()
        {
            var strategy = new DefaultBotStrategy();
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Units = new List<Unit>()
            };

            var ns = new NationState
            {
                Nation = Nation.Europe,
                Treasury = 5
            };

            var londonOnly = TerritoryData.AllTerritories.Where(t => t.Id.Equals("London", StringComparison.OrdinalIgnoreCase)).ToList();

            var imports = strategy.ChooseImports(game, ns, maxImport: 2, londonOnly);

            Assert.NotEmpty(imports);
            Assert.All(imports, imp =>
            {
                Assert.Equal("London", imp.TerritoryId, ignoreCase: true);
                Assert.Equal(UnitType.Fleet, imp.Type);
            });
        }

        [Fact]
        public void NonRlBots_ChooseImports_WithOccupiedTerritory_FavorsArmiesInPortCities()
        {
            var strategy = new DefaultBotStrategy();
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Units = new List<Unit>
                {
                    // Hostile enemy army occupying NewDelhi (India home territory)
                    new Unit { Nation = Nation.China, TerritoryId = "NewDelhi", UnitType = UnitType.Army, IsHostile = true }
                }
            };

            var ns = new NationState
            {
                Nation = Nation.India,
                Treasury = 5
            };

            // Mumbai is a LightBlue port city where both Army and Fleet can be built
            var mumbaiOnly = TerritoryData.AllTerritories.Where(t => t.Id.Equals("Mumbai", StringComparison.OrdinalIgnoreCase)).ToList();

            int armyCount = 0;
            int totalTrials = 100;

            for (int i = 0; i < totalTrials; i++)
            {
                var imports = strategy.ChooseImports(game, ns, maxImport: 1, mumbaiOnly);
                if (imports.Any(u => u.Type == UnitType.Army))
                {
                    armyCount++;
                }
            }

            // Expect ~80% Army probability when occupied (should easily exceed 65%)
            Assert.True(armyCount >= 65, $"Expected at least 65% armies chosen due to occupied territory, but got {armyCount}%");
        }

        [Fact]
        public void NonRlBots_ChooseImports_WithFewerArmyFactoriesThanFleetFactories_FavorsArmiesInPortCities()
        {
            var strategy = new DefaultBotStrategy();
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Units = new List<Unit>(),
                TerritoryStates = new List<TerritoryState>
                {
                    // Has Fleet factory in Mumbai, but 0 Army factories (Delhi has no factory)
                    new TerritoryState { TerritoryId = "Mumbai", HasFactory = true }
                }
            };

            var ns = new NationState
            {
                Nation = Nation.India,
                Treasury = 5
            };

            var mumbaiOnly = TerritoryData.AllTerritories.Where(t => t.Id.Equals("Mumbai", StringComparison.OrdinalIgnoreCase)).ToList();

            int armyCount = 0;
            int totalTrials = 100;

            for (int i = 0; i < totalTrials; i++)
            {
                var imports = strategy.ChooseImports(game, ns, maxImport: 1, mumbaiOnly);
                if (imports.Any(u => u.Type == UnitType.Army))
                {
                    armyCount++;
                }
            }

            // Expect ~80% Army probability when having fewer army factories than fleet factories
            Assert.True(armyCount >= 65, $"Expected at least 65% armies chosen due to factory disparity, but got {armyCount}%");
        }
    }
}
