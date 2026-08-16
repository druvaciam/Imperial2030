using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Helpers;

public static class GameSetupHelper
{
    // Creates nation states, territories and bonds for a new game, deals starting bond packages to players,
    // assigns nation controllers and the investor card holder, and sets starting cash.
    //
    // Everything here is deterministic given the resulting nation->player distribution, EXCEPT the distribution
    // itself, which is randomized (which player gets dealt which package) unless `forcedDistribution` is
    // supplied. Passing the distribution recorded on a game's "StartGame" action (see GameSetupMetadata)
    // reproduces the exact same setup — this is what makes a game replayable from its action log alone.
    //
    // Returns the nation->player distribution that was actually used (random or forced), so callers can log it.
    public static async Task<Dictionary<Nation, Player>> InitializeGameAsync(ApplicationDbContext context, Guid gameId, Dictionary<Nation, Guid>? forcedDistribution = null)
    {
        // PHASE 1: Create Entities
        var newBonds = new List<Bond>();
        var newNationStates = new List<NationState>();

        foreach (Nation nation in Enum.GetValues(typeof(Nation)))
        {
            newNationStates.Add(new NationState { Nation = nation, Treasury = 0, Power = 0, GameId = gameId });
        }

        // Init Territories
        // Each nation starts with 2 factories (one Brown/Army, one LightBlue/Fleet) per Imperial 2030 rules.
        // The remaining 2 home cities can have factories built via the Factory rondel action.
        var startingFactories = new HashSet<string>
        {
            "Moscow", "Vladivostok",       // Russia
            "Beijing", "Shanghai",         // China
            "NewDelhi", "Mumbai",          // India
            "Brasilia", "RioDeJaneiro",    // Brazil
            "Chicago", "NewOrleans",       // USA
            "Paris", "London"              // Europe
        };
        var territories = Imperial2030.Shared.Constants.TerritoryData.AllTerritories;
        var newTerritoryStates = new List<TerritoryState>();
        foreach (var t in territories)
        {
            newTerritoryStates.Add(new TerritoryState { TerritoryId = t.Id, GameId = gameId, HasFactory = startingFactories.Contains(t.Id) });
        }
        context.TerritoryStates.AddRange(newTerritoryStates);

        var bondDefinitions = new[]
        {
            new { Cost = 2, Interest = 1 }, new { Cost = 4, Interest = 2 }, new { Cost = 6, Interest = 3 },
            new { Cost = 9, Interest = 4 }, new { Cost = 12, Interest = 5 }, new { Cost = 16, Interest = 6 },
            new { Cost = 20, Interest = 7 }, new { Cost = 25, Interest = 8 }, new { Cost = 30, Interest = 9 }
        };

        foreach (Nation nation in Enum.GetValues(typeof(Nation)))
        {
            foreach (var def in bondDefinitions)
            {
                newBonds.Add(new Bond { Nation = nation, Cost = def.Cost, Interest = def.Interest, GameId = gameId });
            }
        }

        context.NationStates.AddRange(newNationStates);
        context.Bonds.AddRange(newBonds);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // PHASE 2: Distribution Logic (Official Imperial 2030 Rules)
        var bonds = await context.Bonds.Where(b => b.GameId == gameId).ToListAsync();
        var nationStates = await context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();
        var players = await context.Players.Where(p => p.GameId == gameId).OrderBy(p => p.Id).ToListAsync();

        // Define Packages: Nation -> (Primary 9M Nation, Secondary 2M Nation)
        // Table:
        // Russia -> Russia 9M, China 2M
        // China -> China 9M, India 2M
        // India -> India 9M, Brazil 2M
        // Brazil -> Brazil 9M, USA 2M
        // USA -> USA 9M, Europe 2M
        // Europe -> Europe 9M, Russia 2M
        var packages = new List<(Nation Primary, Nation Secondary)>
        {
            (Nation.Russia, Nation.China),
            (Nation.China, Nation.India),
            (Nation.India, Nation.Brazil),
            (Nation.Brazil, Nation.USA),
            (Nation.USA, Nation.Europe),
            (Nation.Europe, Nation.Russia)
        };

        // Map: Which player gets which packages. Key = Primary Nation of the package, Value = Player who receives it.
        var distribution = new Dictionary<Nation, Player>();

        if (forcedDistribution != null)
        {
            // Replay path: reproduce the exact distribution recorded when this game was originally started.
            foreach (var kvp in forcedDistribution)
            {
                var player = players.First(p => p.Id == kvp.Value);
                distribution[kvp.Key] = player;
            }
        }
        else
        {
            var random = new Random();
            var shuffledPlayers = players.OrderBy(p => random.Next()).ToList();

            if (players.Count == 2)
            {
                // 2 Players: Deal China and Russia.
                // Player A (China): Gets China + Europe + Brazil
                // Player B (Russia): Gets Russia + India + USA
                var p1 = shuffledPlayers[0];
                var p2 = shuffledPlayers[1];

                // Assign explicitly based on rules "China and Russia randomly dealt"
                // Let's assume P1 got China, P2 got Russia (randomness is in shuffledPlayers)

                // P1 Packages
                distribution[Nation.China] = p1;
                distribution[Nation.Europe] = p1;
                distribution[Nation.Brazil] = p1;

                // P2 Packages
                distribution[Nation.Russia] = p2;
                distribution[Nation.India] = p2;
                distribution[Nation.USA] = p2;
            }
            else if (players.Count == 3)
            {
                // 3 Players: Deal India, Russia, China.
                // 1 (p1): India -> Gets India + USA
                // 2 (p2): Russia -> Gets Russia + Brazil
                // 3 (p3): China -> Gets China + Europe
                var p1 = shuffledPlayers[0];
                var p2 = shuffledPlayers[1];
                var p3 = shuffledPlayers[2];

                distribution[Nation.India] = p1;
                distribution[Nation.USA] = p1;

                distribution[Nation.Russia] = p2;
                distribution[Nation.Brazil] = p2;

                distribution[Nation.China] = p3;
                distribution[Nation.Europe] = p3;
            }
            else // 4-6 Players
            {
                // Each receive 1 card.
                // Shuffle packages
                var shuffledPackages = packages.OrderBy(x => random.Next()).ToList();
                for (int i = 0; i < players.Count; i++)
                {
                    // Deal 1 package to each player
                    var pkg = shuffledPackages[i];
                    distribution[pkg.Primary] = shuffledPlayers[i];
                }
                // Remaining packages are "undealt".
            }
        }

        // Execute Transactions for Distributed Packages
        foreach (var kvp in distribution)
        {
            var primaryNation = kvp.Key;
            var player = kvp.Value;

            // Find definition to know secondary
            var def = packages.First(p => p.Primary == primaryNation);

            // Assign 9M Bond (Primary)
            var bond9M = bonds.First(b => b.Nation == def.Primary && b.Cost == 9);
            bond9M.HolderId = player.Id;
            context.Entry(bond9M).State = EntityState.Modified;

            // Credit Treasury for Primary
            var nsPrimary = nationStates.First(ns => ns.Nation == def.Primary);
            nsPrimary.Treasury += 9;
            context.Entry(nsPrimary).State = EntityState.Modified;

            // Assign 2M Bond (Secondary)
            var bond2M = bonds.First(b => b.Nation == def.Secondary && b.Cost == 2);
            bond2M.HolderId = player.Id;
            context.Entry(bond2M).State = EntityState.Modified;

            // Credit Treasury for Secondary
            var nsSecondary = nationStates.First(ns => ns.Nation == def.Secondary);
            nsSecondary.Treasury += 2;
            context.Entry(nsSecondary).State = EntityState.Modified;
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // PHASE 3: Assign Controllers
        // Rule: Controller is who holds the Flag Card.
        // Initially, Flag Card follows the distribution.
        // If not distributed, it goes to holder of 2M bond.
        // If no 2M bond holder, stays in bank (Controller = null).

        // Re-fetch bonds to see current holders
        var bondsHeld = await context.Bonds.Where(b => b.GameId == gameId && b.HolderId != null).ToListAsync();
        var nationStatesToUpdate = await context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();

        foreach (var ns in nationStatesToUpdate)
        {
            Player? controller = null;

            // 1. Check if this Nation package was distributed directly
            if (distribution.ContainsKey(ns.Nation))
            {
                controller = distribution[ns.Nation];
            }
            else
            {
                // 2. Check who owns the 2M bond of this nation
                var bond2M = bondsHeld.FirstOrDefault(b => b.Nation == ns.Nation && b.Cost == 2);
                if (bond2M != null)
                {
                    ns.ControllerId = bond2M.HolderId;
                    context.Entry(ns).State = EntityState.Modified;
                    continue; // Done
                }
            }

            // Reset Rondel Position to null (Off-Board)
            ns.RondelPosition = null;

            if (controller != null)
            {
                ns.ControllerId = controller.Id;
                context.Entry(ns).State = EntityState.Modified;
            }
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Init Investor Card Holder. Rules: starts with the player seated to the left of Russia's controller;
        // if Russia has no controller yet, the player to the left of China's controller instead.
        if (players.Any())
        {
            var sorted = players.GetOrderedPlayers().ToList();
            var gameToInit = await context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
            if (gameToInit != null)
            {
                var russiaNs = gameToInit.NationStates.FirstOrDefault(ns => ns.Nation == Nation.Russia);
                var chinaNs = gameToInit.NationStates.FirstOrDefault(ns => ns.Nation == Nation.China);

                if (russiaNs != null && russiaNs.ControllerId.HasValue)
                {
                    var index = sorted.FindIndex(p => p.Id == russiaNs.ControllerId.Value);
                    var nextIndex = (index + 1) % sorted.Count;
                    gameToInit.InvestorCardHolderId = sorted[nextIndex].Id;
                }
                else if (chinaNs != null && chinaNs.ControllerId.HasValue)
                {
                    var index = sorted.FindIndex(p => p.Id == chinaNs.ControllerId.Value);
                    var nextIndex = (index + 1) % sorted.Count;
                    gameToInit.InvestorCardHolderId = sorted[nextIndex].Id;
                }
                else
                {
                    gameToInit.InvestorCardHolderId = sorted[0].Id;
                }
                context.Entry(gameToInit).State = EntityState.Modified;
            }
        }
        await context.SaveChangesAsync();

        // PHASE 4: Update Game Status and Player Cash
        var gameToUpdate = await context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
        var playersToUpdate = await context.Players.Where(p => p.GameId == gameId).ToListAsync();

        if (gameToUpdate != null)
        {
            gameToUpdate.Status = GameStatus.InProgress;

            int advanceCount = 0;
            while (gameToUpdate.NationStates.FirstOrDefault(ns => ns.Nation == gameToUpdate.CurrentTurnNation)?.ControllerId == null && advanceCount < 6)
            {
                gameToUpdate.AdvanceTurn();
                advanceCount++;
            }

            context.Entry(gameToUpdate).State = EntityState.Modified;
        }

        int startingCash = playersToUpdate.Count switch
        {
            2 => 35,
            3 => 24,
            _ => 13
        };

        foreach (var p in playersToUpdate)
        {
            p.Cash = startingCash;
            // Count how many packages this player received
            int pkgCount = distribution.Values.Count(v => v.Id == p.Id);
            p.Cash -= pkgCount * 11;
            context.Entry(p).State = EntityState.Modified;
        }

        await context.SaveChangesAsync();

        return distribution;
    }
}
