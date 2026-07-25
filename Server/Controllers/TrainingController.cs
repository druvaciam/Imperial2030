using Imperial2030.Server.Models;
using Imperial2030.Server.Data;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Imperial2030.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrainingController : ControllerBase
{
    private static readonly Dictionary<string, TrainingSession> _sessions = new();
    private readonly ApplicationDbContext _context;
    private readonly BotService _botService;

    public TrainingController(ApplicationDbContext context, BotService botService)
    {
        _context = context;
        _botService = botService;
    }

    public class TrainingSession
    {
        public Guid GameId { get; set; }
        public Guid RLPlayerId { get; set; }
    }

    public class ResetResponse
    {
        public string SessionId { get; set; } = "";
        public float[] State { get; set; } = Array.Empty<float>();
        public bool[] ActionMask { get; set; } = Array.Empty<bool>();
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        // Cleanup old finished games to free up memory
        var nMinsAgo = DateTime.UtcNow.AddMinutes(-2);
        var oldGames = await _context.Games
            .Where(g => g.Status == GameStatus.Finished && g.CreatedAt < nMinsAgo)
            .ToListAsync();

        if (oldGames.Any())
        {
            var oldGameIds = oldGames.Select(g => g.Id).ToList();

            _context.GameActions.RemoveRange(_context.GameActions.Where(a => oldGameIds.Contains(a.GameId)));
            _context.Units.RemoveRange(_context.Units.Where(u => oldGameIds.Contains(u.GameId)));
            _context.TerritoryStates.RemoveRange(_context.TerritoryStates.Where(t => oldGameIds.Contains(t.GameId)));
            _context.NationStates.RemoveRange(_context.NationStates.Where(n => oldGameIds.Contains(n.GameId)));
            _context.Bonds.RemoveRange(_context.Bonds.Where(b => oldGameIds.Contains(b.GameId)));
            _context.Players.RemoveRange(_context.Players.Where(p => oldGameIds.Contains(p.GameId)));
            _context.Games.RemoveRange(oldGames);

            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();
        }

        // 1. Create a quick, isolated game in the DB
        var gameId = Guid.NewGuid();
        var game = new Game
        {
            Id = gameId,
            Name = "RL_Training_" + gameId.ToString().Substring(0, 4),
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia
        };

        var randomOpponents = new[] { "Random", "Default" };
        var rng = new Random();
        var players = new List<Player>();
        for (int i = 0; i < 6; i++)
        {
            var p = new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = null, IsBot = true, BotName = $"Bot {i}", Cash = 2 };
            if (i > 0)
            {
                p.BotType = randomOpponents[rng.Next(randomOpponents.Length)];
                p.BotName = $"{p.BotType} Bot {i}";
            }
            players.Add(p);
        }
        var rlPlayer = players[0];
        rlPlayer.BotName = "RLAgent"; // We will control player 0
        rlPlayer.BotType = "RL";

        RLBotStrategy.IsTraining = true;
        game.Players = players;
        game.ActingPlayerId = rlPlayer.Id; // Starts acting

        _context.Games.Add(game);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // --- Official Imperial 2030 Initialization Logic ---
        // PHASE 1: Create Entities
        var newBonds = new List<Bond>();
        var newNationStates = new List<NationState>();

        foreach (Nation nation in Enum.GetValues(typeof(Nation)))
        {
            newNationStates.Add(new NationState { Nation = nation, Treasury = 0, Power = 0, GameId = gameId });
        }

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
        _context.TerritoryStates.AddRange(newTerritoryStates);

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

        _context.NationStates.AddRange(newNationStates);
        _context.Bonds.AddRange(newBonds);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // PHASE 2: Distribution Logic (Official Imperial 2030 Rules)
        var bonds = await _context.Bonds.Where(b => b.GameId == gameId).ToListAsync();
        var nationStates = await _context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();
        var allPlayers = await _context.Players.Where(p => p.GameId == gameId).OrderBy(p => p.Id).ToListAsync();

        var random = new Random();
        var shuffledPlayers = allPlayers.OrderBy(p => random.Next()).ToList();

        var packages = new List<(Nation Primary, Nation Secondary)>
        {
            (Nation.Russia, Nation.China),
            (Nation.China, Nation.India),
            (Nation.India, Nation.Brazil),
            (Nation.Brazil, Nation.USA),
            (Nation.USA, Nation.Europe),
            (Nation.Europe, Nation.Russia)
        };

        var distribution = new Dictionary<Nation, Player>();
        var shuffledPackages = packages.OrderBy(x => random.Next()).ToList();
        for (int i = 0; i < allPlayers.Count; i++)
        {
            var pkg = shuffledPackages[i];
            distribution[pkg.Primary] = shuffledPlayers[i];
        }

        foreach (var kvp in distribution)
        {
            var primaryNation = kvp.Key;
            var player = kvp.Value;
            var def = packages.First(p => p.Primary == primaryNation);

            var bond9M = bonds.First(b => b.Nation == def.Primary && b.Cost == 9);
            bond9M.HolderId = player.Id;
            _context.Entry(bond9M).State = EntityState.Modified;

            var nsPrimary = nationStates.First(ns => ns.Nation == def.Primary);
            nsPrimary.Treasury += 9;
            _context.Entry(nsPrimary).State = EntityState.Modified;

            var bond2M = bonds.First(b => b.Nation == def.Secondary && b.Cost == 2);
            bond2M.HolderId = player.Id;
            _context.Entry(bond2M).State = EntityState.Modified;

            var nsSecondary = nationStates.First(ns => ns.Nation == def.Secondary);
            nsSecondary.Treasury += 2;
            _context.Entry(nsSecondary).State = EntityState.Modified;
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // PHASE 3: Assign Controllers
        var bondsHeld = await _context.Bonds.Where(b => b.GameId == gameId && b.HolderId != null).ToListAsync();
        var nationStatesToUpdate = await _context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();

        foreach (var ns in nationStatesToUpdate)
        {
            Player? controller = null;
            if (distribution.ContainsKey(ns.Nation))
            {
                controller = distribution[ns.Nation];
            }
            else
            {
                var bond2M = bondsHeld.FirstOrDefault(b => b.Nation == ns.Nation && b.Cost == 2);
                if (bond2M != null)
                {
                    ns.ControllerId = bond2M.HolderId;
                    _context.Entry(ns).State = EntityState.Modified;
                    continue;
                }
            }

            ns.RondelPosition = null;
            if (controller != null)
            {
                ns.ControllerId = controller.Id;
                _context.Entry(ns).State = EntityState.Modified;
            }
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        if (allPlayers.Any())
        {
            var sorted = allPlayers.OrderBy(p => p.Id).ToList();
            var gameToInit = await _context.Games.FirstOrDefaultAsync(g => g.Id == gameId);
            if (gameToInit != null)
            {
                gameToInit.InvestorCardHolderId = sorted[0].Id;
                _context.Entry(gameToInit).State = EntityState.Modified;
            }
        }
        await _context.SaveChangesAsync();

        // PHASE 4: Update Game Status and Player Cash
        var gameToUpdate = await _context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
        var playersToUpdate = await _context.Players.Where(p => p.GameId == gameId).ToListAsync();

        if (gameToUpdate != null)
        {
            gameToUpdate.Status = GameStatus.InProgress;
            var firstNs = gameToUpdate.NationStates.FirstOrDefault(ns => ns.Nation == gameToUpdate.CurrentTurnNation);
            if (firstNs == null || !firstNs.ControllerId.HasValue)
            {
                gameToUpdate.AdvanceTurn();
            }
            _context.Entry(gameToUpdate).State = EntityState.Modified;
        }

        int startingCash = 13; // 6 players always in RL training
        foreach (var p in playersToUpdate)
        {
            p.Cash = startingCash;
            int pkgCount = distribution.Values.Count(v => v.Id == p.Id);
            p.Cash -= pkgCount * 11;
            _context.Entry(p).State = EntityState.Modified;
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();


        var sessionId = Guid.NewGuid().ToString();
        _sessions[sessionId] = new TrainingSession { GameId = gameId, RLPlayerId = rlPlayer.Id };

        var state = GetStateVector(gameId, rlPlayer.Id);
        var mask = GetActionMask(gameId, rlPlayer.Id);

        return Ok(new ResetResponse { SessionId = sessionId, State = state, ActionMask = mask });
    }

    public class StepRequest
    {
        public string SessionId { get; set; } = "";
        public int Action { get; set; }
    }

    public class StepResponse
    {
        public float[] State { get; set; } = Array.Empty<float>();
        public float Reward { get; set; }
        public bool Done { get; set; }
        public bool[] ActionMask { get; set; } = Array.Empty<bool>();
    }

    [HttpPost("step")]
    public async Task<IActionResult> Step([FromBody] StepRequest req)
    {
        if (!_sessions.TryGetValue(req.SessionId, out var session)) return NotFound();

        var game = await _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == session.GameId);
        if (game == null) return NotFound();

        var player = game.Players.First(p => p.Id == session.RLPlayerId);
        float reward = 0;

        // Apply Macro Action through BotService
        RLBotStrategy.TrainingActionOverride.Value = req.Action;
        _botService.SkipDelays = true;
        await _botService.TryPlayBotTurnAsync(game.Id, singleTurnOnly: true);
        RLBotStrategy.TrainingActionOverride.Value = null; // Clean up

        await _context.SaveChangesAsync();

        // Wait until it's the RL agent's turn again, or game ends
        bool done = await AdvanceUntilRLTurn(game.Id, session.RLPlayerId);
        game = await _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstAsync(g => g.Id == session.GameId);

        if (done || game.Status == GameStatus.Finished)
        {
            // Calculate final reward (Victory Points)
            reward += CalculateVP(game, session.RLPlayerId);
            return Ok(new StepResponse { State = GetStateVector(game.Id, session.RLPlayerId), Reward = reward, Done = true, ActionMask = new bool[10] });
        }

        return Ok(new StepResponse { State = GetStateVector(game.Id, session.RLPlayerId), Reward = reward, Done = false, ActionMask = GetActionMask(game.Id, session.RLPlayerId) });
    }

    private async Task<bool> AdvanceUntilRLTurn(Guid gameId, Guid rlPlayerId)
    {
        int safety = 0;
        while (safety++ < 1000)
        {
            _context.ChangeTracker.Clear(); // CRITICAL: Prevent EF from caching the old CurrentTurnNation

            var g = _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstOrDefault(x => x.Id == gameId);
            if (g == null || g.Status == GameStatus.Finished) return true;

            // If it's RL's turn and they need to act (investor or nation controller)
            if (g.IsInvestorTurn && g.ActingPlayerId == rlPlayerId) return false;

            if (!g.IsInvestorTurn && !g.PendingBattleDefenders.Any())
            {
                var ns = g.NationStates.FirstOrDefault(n => n.Nation == g.CurrentTurnNation);
                if (ns != null && ns.ControllerId == rlPlayerId) return false;
            }

            // It's another bot's turn, let BotService play it
            await _botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);
        }
        return true;
    }

    private float CalculateVP(Game game, Guid playerId)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return 0;

        float score = player.Cash;

        var bonds = _context.Bonds.Where(b => b.HolderId == playerId).ToList();
        foreach (var bond in bonds)
        {
            var nation = game.NationStates.FirstOrDefault(n => n.Nation == bond.Nation);
            if (nation != null)
            {
                int factor = nation.Power / 5;
                score += bond.Interest * factor;
            }
        }

        return score;
    }

    private float[] GetStateVector(Guid gameId, Guid rlPlayerId)
    {
        var game = _context.Games.Include(g => g.NationStates).Include(g => g.Players).FirstOrDefault(g => g.Id == gameId);
        if (game == null) return new float[20];

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        float[] state = new float[20];
        if (rlPlayer == null) return state;

        var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };

        int i = 0;
        foreach (var nation in imperial2030Nations)
        {
            var ns = game.NationStates.FirstOrDefault(n => n.Nation == nation);
            if (ns != null)
            {
                state[i++] = ns.Power;
                state[i++] = ns.Treasury;
                state[i++] = ns.ControllerId == rlPlayerId ? 1.0f : 0.0f;
            }
            else
            {
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
            }
        }
        state[18] = rlPlayer.Cash;
        state[19] = game.IsInvestorTurn ? 1.0f : 0.0f;

        return state;
    }

    private bool[] GetActionMask(Guid gameId, Guid rlPlayerId)
    {
        var mask = new bool[8];
        var game = _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
        if (game == null) return mask;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return mask;

        if (game.IsInvestorTurn)
        {
            mask[7] = true;
            return mask;
        }

        var ns = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        if (ns == null || ns.ControllerId != rlPlayerId) return mask;

        mask[0] = IsSlotValid(ns, rlPlayer, 1);
        mask[1] = IsSlotValid(ns, rlPlayer, 2) || IsSlotValid(ns, rlPlayer, 6);
        mask[2] = IsSlotValid(ns, rlPlayer, 5);
        mask[3] = IsSlotValid(ns, rlPlayer, 3) || IsSlotValid(ns, rlPlayer, 7);
        mask[4] = mask[3];
        mask[5] = IsSlotValid(ns, rlPlayer, 0);
        mask[6] = IsSlotValid(ns, rlPlayer, 4);
        mask[7] = false;

        if (!mask.Any(m => m)) mask[5] = true;

        return mask;
    }

    private bool IsSlotValid(NationState ns, Player rlPlayer, int targetSlot)
    {
        if (ns.RondelPosition.HasValue && ns.RondelPosition.Value == targetSlot) return false;

        int moveCost = 0;
        if (ns.RondelPosition.HasValue)
        {
            int dist = (targetSlot - ns.RondelPosition.Value + 8) % 8;
            if (dist > 3)
            {
                int pf = ns.Power / 5;
                moveCost = (dist - 3) * (1 + pf);
            }
        }
        return rlPlayer.Cash >= moveCost;
    }
}
