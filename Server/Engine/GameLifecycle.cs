using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Outcome of setting a game up. <see cref="Distribution"/> is the nation flag card -> player deal that
/// was used (random or forced), which the <c>StartGame</c> log entry carries so the game can be set up
/// again identically from its action log.
/// </summary>
public sealed record StartOutcome(bool Ok, string? Error, Dictionary<Nation, Guid> Distribution) : EngineResult(Ok, Error)
{
    public static new StartOutcome Fail(string error) => new(false, error, new Dictionary<Nation, Guid>());
}

/// <summary>
/// Set-up. Imperial-2030-Rules.pdf p.4-5: each nation starts with two factories; the nation flag cards
/// are dealt by player count (6 with 4-6 players one each, 3 with three players, 2 with two) and each
/// carries the bonds on its back - the nation's 9M and the 2M of the next nation round - paid into the
/// treasuries; "each player will have 2 million remaining"; an undealt card goes to the holder of that
/// nation's 2M bond, or stays in the bank; the investor card goes to the player left of Russia's
/// government, or of China's if Russia has none.
/// </summary>
public static class GameLifecycle
{
    private static readonly HashSet<string> StartingFactories = new()
    {
        "Moscow", "Vladivostok",       // Russia
        "Beijing", "Shanghai",         // China
        "NewDelhi", "Mumbai",          // India
        "Brasilia", "RioDeJaneiro",    // Brazil
        "Chicago", "NewOrleans",       // USA
        "Paris", "London"              // Europe
    };

    private static readonly (int Cost, int Interest)[] BondDefinitions =
    {
        (2, 1), (4, 2), (6, 3), (9, 4), (12, 5), (16, 6), (20, 7), (25, 8), (30, 9)
    };

    /// <summary>The nation flag cards: each carries the nation's 9M bond and the 2M bond of the next.</summary>
    private static readonly (Nation Primary, Nation Secondary)[] Packages =
    {
        (Nation.Russia, Nation.China),
        (Nation.China, Nation.India),
        (Nation.India, Nation.Brazil),
        (Nation.Brazil, Nation.USA),
        (Nation.USA, Nation.Europe),
        (Nation.Europe, Nation.Russia)
    };

    /// <summary>
    /// Sets <paramref name="game"/> up for its players (already in <see cref="Game.Players"/>): creates its
    /// nation states, territory states and bonds, deals the flag cards and starting bonds, assigns the
    /// governments and the investor card, sets the starting cash, opens the first turn and logs
    /// <c>StartGame</c>. The deal is random unless <paramref name="forcedDistribution"/> gives it, as the
    /// replay of a logged game does. The <c>StartGame</c> entry's roster is taken from the players unless
    /// <paramref name="roster"/> supplies it - an import carries the source game's roster, remapped onto
    /// the players it just created. Does not save or broadcast; lobby and host checks are the caller's.
    /// </summary>
    public static StartOutcome Start(ApplicationDbContext? context, Game game, Dictionary<Nation, Guid>? forcedDistribution, string startedBy, List<PlayerRosterEntry>? roster = null)
    {
        if (game.Players.Count < 2) return StartOutcome.Fail("Need at least 2 players to start.");
        if (game.NationStates.Any() || game.Bonds.Any()) return StartOutcome.Fail("Game is already set up.");

        // Seating order is Player.Id order everywhere (PlayerHelper.GetOrderedPlayers).
        var players = game.Players.GetOrderedPlayers().ToList();

        // The board: nations, territories with the starting factories, and the bond piles.
        var nationStates = Enum.GetValues<Nation>()
            .Select(n => new NationState { Nation = n, Treasury = 0, Power = 0, GameId = game.Id })
            .ToList();
        var territoryStates = TerritoryData.AllTerritories
            .Select(t => new TerritoryState { TerritoryId = t.Id, GameId = game.Id, HasFactory = StartingFactories.Contains(t.Id) })
            .ToList();
        var bonds = Enum.GetValues<Nation>()
            .SelectMany(n => BondDefinitions.Select(d => new Bond { Nation = n, Cost = d.Cost, Interest = d.Interest, GameId = game.Id }))
            .ToList();
        foreach (var ns in nationStates) game.NationStates.Add(ns);
        foreach (var ts in territoryStates) game.TerritoryStates.Add(ts);
        foreach (var b in bonds) game.Bonds.Add(b);
        if (context != null)
        {
            context.NationStates.AddRange(nationStates);
            context.TerritoryStates.AddRange(territoryStates);
            context.Bonds.AddRange(bonds);
        }

        // Dealing the flag cards: which player gets which package, keyed by the package's nation.
        var distribution = new Dictionary<Nation, Player>();
        if (forcedDistribution != null)
        {
            foreach (var kvp in forcedDistribution)
            {
                var player = players.FirstOrDefault(p => p.Id == kvp.Value);
                if (player == null) return StartOutcome.Fail($"Distribution names a player not in the game: {kvp.Value}.");
                distribution[kvp.Key] = player;
            }
        }
        else
        {
            var random = new Random();
            var shuffledPlayers = players.OrderBy(p => random.Next()).ToList();

            if (players.Count == 2)
            {
                // p.5: China and Russia dealt at random; A: Europe and Brazil to China, B: India and USA to Russia.
                var p1 = shuffledPlayers[0];
                var p2 = shuffledPlayers[1];
                distribution[Nation.China] = p1;
                distribution[Nation.Europe] = p1;
                distribution[Nation.Brazil] = p1;
                distribution[Nation.Russia] = p2;
                distribution[Nation.India] = p2;
                distribution[Nation.USA] = p2;
            }
            else if (players.Count == 3)
            {
                // p.5: India, Russia and China dealt at random; 1: USA to India, 2: Brazil to Russia, 3: Europe to China.
                distribution[Nation.India] = shuffledPlayers[0];
                distribution[Nation.USA] = shuffledPlayers[0];
                distribution[Nation.Russia] = shuffledPlayers[1];
                distribution[Nation.Brazil] = shuffledPlayers[1];
                distribution[Nation.China] = shuffledPlayers[2];
                distribution[Nation.Europe] = shuffledPlayers[2];
            }
            else
            {
                // p.4: 4 to 6 players - one card each; with 4 or 5 the rest stay undealt.
                var shuffledPackages = Packages.OrderBy(x => random.Next()).ToList();
                for (int i = 0; i < players.Count; i++)
                {
                    distribution[shuffledPackages[i].Primary] = shuffledPlayers[i];
                }
            }
        }

        // Each dealt card: take the two bonds on its back and pay their price into the treasuries.
        foreach (var kvp in distribution)
        {
            var def = Packages.First(p => p.Primary == kvp.Key);
            var player = kvp.Value;

            bonds.First(b => b.Nation == def.Primary && b.Cost == 9).HolderId = player.Id;
            nationStates.First(ns => ns.Nation == def.Primary).Treasury += 9;

            bonds.First(b => b.Nation == def.Secondary && b.Cost == 2).HolderId = player.Id;
            nationStates.First(ns => ns.Nation == def.Secondary).Treasury += 2;
        }

        // Governments: the dealt card's holder; an undealt card goes to the holder of the nation's 2M bond
        // (p.5, "since they have the highest credit sum"), or stays in the bank.
        foreach (var ns in nationStates)
        {
            if (distribution.TryGetValue(ns.Nation, out var dealtTo))
            {
                ns.ControllerId = dealtTo.Id;
            }
            else
            {
                ns.ControllerId = bonds.FirstOrDefault(b => b.Nation == ns.Nation && b.Cost == 2 && b.HolderId != null)?.HolderId;
            }
            ns.RondelPosition = null;
        }

        // The investor card (p.5): left of Russia's government, or of China's if Russia has none.
        var russiaGov = nationStates.First(ns => ns.Nation == Nation.Russia).ControllerId;
        var chinaGov = nationStates.First(ns => ns.Nation == Nation.China).ControllerId;
        var anchor = russiaGov ?? chinaGov;
        if (anchor.HasValue)
        {
            int index = players.FindIndex(p => p.Id == anchor.Value);
            game.InvestorCardHolderId = players[(index + 1) % players.Count].Id;
        }
        else
        {
            game.InvestorCardHolderId = players[0].Id;
        }

        // Starting cash (p.4-5) less the 11M each dealt card's bonds cost.
        int startingCash = players.Count switch
        {
            2 => 35,
            3 => 24,
            _ => 13
        };
        foreach (var p in players)
        {
            p.Cash = startingCash - 11 * distribution.Values.Count(v => v.Id == p.Id);
        }

        game.Status = GameStatus.InProgress;
        game.StartedAt ??= DateTime.UtcNow;

        // Russia opens (p.7); a nation without a government is skipped.
        int advanceCount = 0;
        while (game.NationStates.FirstOrDefault(ns => ns.Nation == game.CurrentTurnNation)?.ControllerId == null && advanceCount < 6)
        {
            game.AdvanceTurn();
            advanceCount++;
        }

        if (context != null)
        {
            context.Entry(game).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
            foreach (var p in players) context.Entry(p).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        }

        var dealt = distribution.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Id);
        roster ??= players.Select(p => new PlayerRosterEntry
        {
            PlayerId = p.Id,
            UserId = p.UserId,
            IsHost = p.IsHost,
            IsBot = p.IsBot,
            BotName = p.BotName,
            BotType = p.BotType,
            DisplayName = p.GetPlayerName(context)
        }).ToList();
        GameLogger.LogStartGame(context, game, startedBy, dealt, roster);

        return new StartOutcome(true, null, dealt);
    }
}
