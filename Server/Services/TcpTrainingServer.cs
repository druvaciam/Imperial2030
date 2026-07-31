using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Services;

public class TcpTrainingServer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BotService _botService;
    private readonly ILogger<TcpTrainingServer> _logger;

    private static readonly Dictionary<string, TrainingSession> _sessions = new();

    public TcpTrainingServer(IServiceProvider serviceProvider, BotService botService, ILogger<TcpTrainingServer> logger)
    {
        _serviceProvider = serviceProvider;
        _botService = botService;
        _logger = logger;
    }

    public class TrainingSession
    {
        public Guid GameId { get; set; }
        public Guid RLPlayerId { get; set; }
    }

    public class TcpRequest
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = ""; // "reset" or "step"

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("action")]
        public int Action { get; set; }
    }

    public class ResetResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("state")]
        public float[] State { get; set; } = Array.Empty<float>();

        [JsonPropertyName("actionMask")]
        public bool[] ActionMask { get; set; } = Array.Empty<bool>();
    }

    public class StepResponse
    {
        [JsonPropertyName("state")]
        public float[] State { get; set; } = Array.Empty<float>();

        [JsonPropertyName("reward")]
        public float Reward { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("actionMask")]
        public bool[] ActionMask { get; set; } = Array.Empty<bool>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 5295);
        listener.Start();
        _logger.LogInformation("TcpTrainingServer listening on 127.0.0.1:5295");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken); // Fire and forget
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        using (var networkStream = client.GetStream())
        using (var reader = new StreamReader(networkStream))
        using (var writer = new StreamWriter(networkStream) { AutoFlush = true })
        {
            _logger.LogInformation("RL Client connected");
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line == null) break; // Client disconnected

                    var req = JsonSerializer.Deserialize<TcpRequest>(line);
                    if (req == null) continue;

                    if (req.Command == "reset")
                    {
                        var res = await HandleResetAsync();
                        await writer.WriteLineAsync(JsonSerializer.Serialize(res));
                    }
                    else if (req.Command == "step")
                    {
                        var res = await HandleStepAsync(req);
                        if (res != null)
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(res));
                        }
                        else
                        {
                            await writer.WriteLineAsync("{}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling RL client");
            }
            _logger.LogInformation("RL Client disconnected");
        }
    }

    private async Task<ResetResponse> HandleResetAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Cleanup old finished games to free up memory immediately
        var oldGames = await _context.Games
            .Where(g => g.Status == GameStatus.Finished)
            .ToListAsync();

        if (oldGames.Count > 0)
        {
            var oldGameIds = oldGames.Select(g => g.Id).ToList();

            var gameActions = await _context.GameActions.Where(a => oldGameIds.Contains(a.GameId)).ToListAsync();
            _context.GameActions.RemoveRange(gameActions);

            var units = await _context.Units.Where(u => oldGameIds.Contains(u.GameId)).ToListAsync();
            _context.Units.RemoveRange(units);

            var territoryStates = await _context.TerritoryStates.Where(t => oldGameIds.Contains(t.GameId)).ToListAsync();
            _context.TerritoryStates.RemoveRange(territoryStates);

            var oldNationStates = await _context.NationStates.Where(n => oldGameIds.Contains(n.GameId)).ToListAsync();
            _context.NationStates.RemoveRange(oldNationStates);

            var oldBonds = await _context.Bonds.Where(b => oldGameIds.Contains(b.GameId)).ToListAsync();
            _context.Bonds.RemoveRange(oldBonds);

            var oldPlayers = await _context.Players.Where(p => oldGameIds.Contains(p.GameId)).ToListAsync();
            _context.Players.RemoveRange(oldPlayers);

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

        var randomOpponents = new[] { "Random", "Default" };// "Greedy", "Aggressive", "Friendly" };
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
        var territories = TerritoryData.AllTerritories;
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
            var gameToInit = await _context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
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
                _context.Entry(gameToInit).State = EntityState.Modified;
            }
        }
        await _context.SaveChangesAsync();

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

        int startingCash = 13;
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

        var state = GetStateVector(_context, gameId, rlPlayer.Id);
        var mask = GetActionMask(_context, gameId, rlPlayer.Id);

        return new ResetResponse { SessionId = sessionId, State = state, ActionMask = mask };
    }

    private async Task<StepResponse?> HandleStepAsync(TcpRequest req)
    {
        if (!_sessions.TryGetValue(req.SessionId, out var session)) return null;

        using var scope = _serviceProvider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var game = await _context.Games.Include(g => g.Players).Include(g => g.NationStates).Include(g => g.Bonds).FirstOrDefaultAsync(g => g.Id == session.GameId);
        if (game == null) return null;

        var player = game.Players.First(p => p.Id == session.RLPlayerId);

        float prevVP = CalculateRelativeVP(_context, game, session.RLPlayerId);

        // Snapshot pre-turn state to detect wasted actions
        var preNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        int? preTreasury = preNs?.Treasury;
        int? preRondelPos = preNs?.RondelPosition;
        bool wasRondelTurn = !game.IsInvestorTurn && !game.PendingBattleDefenders.Any()
                             && preNs != null && preNs.ControllerId == session.RLPlayerId;

        RLBotStrategy.TrainingActionOverride.Value = req.Action;
        _botService.SkipDelays = true;
        await _botService.TryPlayBotTurnAsync(game.Id, singleTurnOnly: true);
        RLBotStrategy.TrainingActionOverride.Value = null; // Clean up

        await _context.SaveChangesAsync();

        bool done = await AdvanceUntilRLTurn(_context, game.Id, session.RLPlayerId);
        game = await _context.Games.Include(g => g.Players).Include(g => g.NationStates).Include(g => g.Bonds).FirstAsync(g => g.Id == session.GameId);

        float newVP = CalculateRelativeVP(_context, game, session.RLPlayerId);
        float reward = newVP - prevVP;

        // Continuous reward for leading the game (or penalty for trailing)
        // This gives the agent a dense gradient to always try and increase its relative score, even if it's currently losing
        reward += newVP * 0.05f;

        // Penalty for wasted Rondel turns (e.g., picking Factory with no money)
        // Rondel slots: 0=Taxation, 1=Factory, 2=Production, 3=Maneuver, 4=Investor, 5=Import, 6=Production, 7=Maneuver
        if (wasRondelTurn && req.Action >= 0 && req.Action <= 5 && preRondelPos.HasValue)
        {
            int targetSlot = (preRondelPos.Value + req.Action + 1) % 8;
            // Factory (slot 1) wasted: not enough treasury OR no valid cities to build in
            if (targetSlot == 1 && preNs != null)
            {
                bool noMoney = preTreasury.HasValue && preTreasury.Value < 5;
                bool allBuiltOrBlocked = false;
                if (!noMoney)
                {
                    var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation && t.CityType != CityType.None);
                    allBuiltOrBlocked = homeCities.All(city =>
                    {
                        var ts = _context.TerritoryStates.FirstOrDefault(t => t.GameId == game.Id && t.TerritoryId == city.Id);
                        if (ts != null && ts.HasFactory) return true; // Already built
                        bool blocked = _context.Units.Any(u => u.GameId == game.Id && u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != preNs.Nation && u.IsHostile);
                        return blocked; // Blocked by enemy
                    });
                }
                if (noMoney || allBuiltOrBlocked)
                {
                    _logger.LogWarning($"[RL PENALTY] Wasted Factory action by {preNs.Nation}. NoMoney: {noMoney}, AllBuiltOrBlocked: {allBuiltOrBlocked}");
                    reward -= 5.0f;
                }
            }
            if (targetSlot == 5 && preTreasury.HasValue && preTreasury.Value < 1)
            {
                _logger.LogWarning($"[RL PENALTY] Wasted Import action by {preNs?.Nation}. Treasury < 1.");
                reward -= 5.0f;
            }
            // Maneuver (slot 3 or 7) with 0 units = wasted turn
            if ((targetSlot == 3 || targetSlot == 7) && preNs != null)
            {
                bool hasUnits = _context.Units.Any(u => u.GameId == game.Id && u.Nation == preNs.Nation);
                if (!hasUnits)
                {
                    _logger.LogWarning($"[RL PENALTY] Wasted Maneuver action by {preNs.Nation}. No units to move.");
                    reward -= 5.0f;
                }
            }
        }

        var allScores = game.Players.Select(p => new { p.Id, Score = game.CalculateScore(p.Id) }).ToList();
        float maxOfOthersScore = allScores.Where(s => s.Id != session.RLPlayerId).Max(s => s.Score);
        float rlScore = allScores.First(s => s.Id == session.RLPlayerId).Score;

        //_logger.LogInformation($"RL player scored {rlScore} and max of others score is {maxOfOthersScore}, intermediate score: {newVP}, prev {prevVP}, step reward: {reward}, gamestatus: {game.Status}, done: {done}");

        if (game.Status == GameStatus.Finished)
        {
            _logger.LogInformation($"Finished! RL player scored {rlScore} and max of others score is {maxOfOthersScore}, intermediate score: {newVP}, reward: {reward}");

            // At the end of the game, reward perfectly aligns with the final VP difference
            reward += (rlScore - maxOfOthersScore) * 1.0f;

            // Small flat bonus for winning
            if (rlScore > maxOfOthersScore)
            {
                reward += 100f;
            }

            _logger.LogInformation($"Final reward is {reward}, game players: {string.Join(", ", game.Players.Select(p => p.BotType))}");

            var stateResponse = GetStateVector(_context, game.Id, session.RLPlayerId);

            // Immediately cleanup the finished game to prevent memory buildup and 10-second freezes
            var gameActions = await _context.GameActions.Where(a => a.GameId == game.Id).ToListAsync();
            _context.GameActions.RemoveRange(gameActions);

            var units = await _context.Units.Where(u => u.GameId == game.Id).ToListAsync();
            _context.Units.RemoveRange(units);

            var territoryStates = await _context.TerritoryStates.Where(t => t.GameId == game.Id).ToListAsync();
            _context.TerritoryStates.RemoveRange(territoryStates);

            var nationStates = await _context.NationStates.Where(n => n.GameId == game.Id).ToListAsync();
            _context.NationStates.RemoveRange(nationStates);

            var bondsHeld = await _context.Bonds.Where(b => b.GameId == game.Id).ToListAsync();
            _context.Bonds.RemoveRange(bondsHeld);

            var gamePlayers = await _context.Players.Where(p => p.GameId == game.Id).ToListAsync();
            _context.Players.RemoveRange(gamePlayers);

            _context.Games.Remove(game);
            await _context.SaveChangesAsync();

            return new StepResponse { State = stateResponse, Reward = reward, Done = true, ActionMask = new bool[64] };
        }

        return new StepResponse { State = GetStateVector(_context, game.Id, session.RLPlayerId), Reward = reward, Done = false, ActionMask = GetActionMask(_context, game.Id, session.RLPlayerId) };
    }

    private async Task<bool> AdvanceUntilRLTurn(ApplicationDbContext _context, Guid gameId, Guid rlPlayerId)
    {
        int safety = 0;
        while (safety++ < 1000)
        {
            _context.ChangeTracker.Clear();

            var g = _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstOrDefault(x => x.Id == gameId);
            if (g == null || g.Status == GameStatus.Finished) return true;

            if (g.IsInvestorTurn && g.ActingPlayerId == rlPlayerId) return false;

            if (g.PendingBattleDefenders.Any())
            {
                bool rlIsDefender = g.PendingBattleDefenders.Any(def =>
                    g.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));
                if (rlIsDefender) return false;
            }
            else if (!g.IsInvestorTurn)
            {
                var ns = g.NationStates.FirstOrDefault(n => n.Nation == g.CurrentTurnNation);
                if (ns != null && ns.ControllerId == rlPlayerId) return false;
            }

            await _botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);
        }
        return true;
    }

    private float CalculateRelativeVP(ApplicationDbContext _context, Game game, Guid playerId)
    {
        var allScores = game.Players.Select(p => new { p.Id, Score = CalculateVP(_context, game, p.Id, useDense: true) }).ToList();
        float myScore = allScores.First(s => s.Id == playerId).Score;
        float maxOtherScore = allScores.Where(s => s.Id != playerId).Max(s => s.Score);
        return myScore - maxOtherScore;
    }

    private float CalculateVP(ApplicationDbContext _context, Game game, Guid playerId, bool useDense = true)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return 0;

        float score = player.Cash * .9f;

        var bonds = _context.Bonds.Where(b => b.HolderId == playerId).ToList();
        foreach (var bond in bonds)
        {
            var nation = game.NationStates.FirstOrDefault(n => n.Nation == bond.Nation);
            if (nation != null)
            {
                if (useDense)
                {
                    var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == nation.Nation).Select(t => t.Id).ToList();
                    float factoryScore = 0;

                    foreach (var terrId in homeTerritories)
                    {
                        var ts = _context.TerritoryStates.FirstOrDefault(t => t.GameId == game.Id && t.TerritoryId == terrId);
                        bool isOccupied = _context.Units.Any(u => u.GameId == game.Id && u.TerritoryId == terrId && u.Nation != nation.Nation && u.IsHostile);

                        if (ts != null && ts.HasFactory)
                        {
                            if (isOccupied) factoryScore += 0.02f; // Suppressed factory
                            else factoryScore += 0.2f; // Healthy factory
                        }
                        else
                        {
                            if (isOccupied) factoryScore -= 0.1f; // Enemy blocking factory building
                        }
                    }

                    int flagCount = _context.TerritoryStates.Count(t => t.GameId == game.Id && t.Controller == nation.Nation);
                    int unitCount = _context.Units.Count(u => u.GameId == game.Id && u.Nation == nation.Nation);

                    float denseFactor = (nation.Power / 5.0f);

                    if (nation.ControllerId == playerId)
                    {
                        float flagValue = 0.02f;
                        int distanceToTax = (8 - (nation.RondelPosition ?? 0)) % 8;
                        if (distanceToTax == 0) distanceToTax = 8; // If currently on Taxation, it's 8 steps away
                        if (distanceToTax <= 3) flagValue = 0.04f; // More valuable when close to tax

                        denseFactor += factoryScore
                                     + (flagCount * flagValue)
                                     + (unitCount * 0.01f)
                                     + (nation.Treasury * 0.005f);
                    }

                    score += bond.Interest * denseFactor;
                }
                else
                {
                    score = game.CalculateScore(playerId);
                    break;
                }
            }
        }

        return score;
    }

    private float[] GetStateVector(ApplicationDbContext _context, Guid gameId, Guid rlPlayerId)
    {
        float[] state = new float[RLBotStrategy.StateSize];

        var game = _context.Games.Include(g => g.NationStates).Include(g => g.Players).Include(g => g.Bonds).FirstOrDefault(g => g.Id == gameId);
        if (game == null) return state;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return state;

        var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
        var bonds = game.Bonds.ToList();

        var allTerritories = _context.TerritoryStates.Where(t => t.GameId == gameId).ToList();
        var allUnits = _context.Units.Where(u => u.GameId == gameId).ToList();

        var allPlayers = game.Players.Select(p => new
        {
            Player = p,
            Score = game.CalculateScore(p.Id)
        })
        .OrderByDescending(x => x.Player.Id == rlPlayerId ? 1 : 0)
        .ThenBy(x => x.Player.Id)
        .ToList();

        var sortedOpponents = allPlayers.Where(x => x.Player.Id != rlPlayerId).ToList();

        int i = 0;
        foreach (var nation in imperial2030Nations)
        {
            var ns = game.NationStates.FirstOrDefault(n => n.Nation == nation);
            if (ns != null)
            {
                state[i++] = ns.Power / 25.0f;
                state[i++] = ns.Treasury / 30.0f;
                state[i++] = ns.RondelPosition.HasValue ? ns.RondelPosition.Value / 7.0f : -1.0f;

                var bondCosts = new[] { 2, 4, 6, 9, 12, 16, 20, 25, 30 };
                foreach (var cost in bondCosts)
                {
                    var bond = bonds.FirstOrDefault(b => b.Nation == nation && b.Cost == cost);
                    state[i++] = (bond == null || !bond.HolderId.HasValue) ? 1.0f : 0.0f; // Unowned
                    state[i++] = (bond != null && bond.HolderId == rlPlayerId) ? 1.0f : 0.0f; // Me
                    for (int oppIdx = 0; oppIdx < 5; oppIdx++) // 5 Opponents
                    {
                        if (oppIdx < sortedOpponents.Count)
                        {
                            var oppId = sortedOpponents[oppIdx].Player.Id;
                            state[i++] = (bond != null && bond.HolderId == oppId) ? 1.0f : 0.0f;
                        }
                        else
                        {
                            state[i++] = 0.0f;
                        }
                    }
                }

                var homeTerritories = TerritoryData.AllTerritories.Where(x => x.Nation == nation).OrderBy(x => x.Id).ToList();
                for (int tIdx = 0; tIdx < 4; tIdx++)
                {
                    if (tIdx < homeTerritories.Count)
                    {
                        var tData = homeTerritories[tIdx];
                        var ts = allTerritories.FirstOrDefault(t => t.TerritoryId == tData.Id);
                        bool hasFactory = ts != null && ts.HasFactory;
                        bool isOccupied = allUnits.Any(u => u.TerritoryId == tData.Id && u.Nation != nation && u.IsHostile);

                        state[i++] = (tData.CityType == CityType.Brown) ? 1.0f : 0.0f; // Is Brown (0 means Blue)
                        state[i++] = hasFactory ? 1.0f : 0.0f; // Is Built
                        state[i++] = isOccupied ? 1.0f : 0.0f; // Occupied
                    }
                    else
                    {
                        for (int j = 0; j < 3; j++) state[i++] = 0;
                    }
                }

                state[i++] = allTerritories.Count(t => t.Controller == nation) / 15.0f;
                state[i++] = allUnits.Count(u => u.Nation == nation && u.UnitType == UnitType.Army) / 10.0f;
                state[i++] = allUnits.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet) / 10.0f;

                // Add 4 boolean flags for action validity
                bool noMoney = ns.Treasury < 5;
                bool allBuiltOrBlocked = homeTerritories.All(city =>
                {
                    var ts = allTerritories.FirstOrDefault(t => t.TerritoryId == city.Id);
                    if (ts != null && ts.HasFactory) return true; // Already built
                    bool blocked = allUnits.Any(u => u.TerritoryId == city.Id && u.Nation != ns.Nation && u.IsHostile);
                    return blocked; // Blocked by enemy
                });
                state[i++] = (!noMoney && !allBuiltOrBlocked) ? 1.0f : 0.0f; // Can build factory
                state[i++] = (allUnits.Any(u => u.Nation == nation)) ? 1.0f : 0.0f; // Has units for maneuver
                state[i++] = (ns.Treasury >= 1) ? 1.0f : 0.0f; // Has at least 1m (can import 1)
                state[i++] = (ns.Treasury >= 3) ? 1.0f : 0.0f; // Has at least 3m (can import 3)
            }
            else
            {
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = -1.0f;
                for (int j = 0; j < 63; j++) state[i++] = 0; // Bond binary parameters
                for (int j = 0; j < 12; j++) state[i++] = 0; // Territory states
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
                for (int j = 0; j < 4; j++) state[i++] = 0; // The 4 boolean flags
            }
        }

        foreach (var nation in imperial2030Nations)
        {
            state[i++] = game.CurrentTurnNation == nation ? 1.0f : 0.0f;
        }

        state[i++] = rlPlayer.Cash / 50.0f;
        state[i++] = game.IsInvestorTurn ? 1.0f : 0.0f;

        // Global Scoreboard
        for (int pIdx = 0; pIdx < 6; pIdx++)
        {
            if (pIdx < allPlayers.Count)
            {
                var pData = allPlayers[pIdx];
                state[i++] = pData.Player.Id == rlPlayerId ? 1.0f : 0.0f;
                state[i++] = pData.Score / 100.0f;
                state[i++] = pData.Player.Cash / 50.0f;
                foreach (var nation in imperial2030Nations)
                {
                    var ns = game.NationStates.FirstOrDefault(n => n.Nation == nation);
                    state[i++] = (ns != null && ns.ControllerId == pData.Player.Id) ? 1.0f : 0.0f;
                }
                state[i++] = pData.Player.Id == game.InvestorCardHolderId ? 1.0f : 0.0f;
            }
            else
            {
                // Pad with zeros for missing players
                state[i++] = 0f;
                state[i++] = 0f;
                state[i++] = 0f;
                for (int nIdx = 0; nIdx < 6; nIdx++) state[i++] = 0f;
                state[i++] = 0f;
            }
        }

        // Pending Battle Context (13 floats)
        var defNationToResolve = game.PendingBattleDefenders.FirstOrDefault(def =>
            game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));

        if (defNationToResolve != default)
        {
            state[i++] = 1.0f;
            foreach (var nation in imperial2030Nations)
                state[i++] = game.PendingBattleAggressorNation == nation ? 1.0f : 0.0f;
            foreach (var nation in imperial2030Nations)
                state[i++] = defNationToResolve == nation ? 1.0f : 0.0f;
        }
        else
        {
            state[i++] = 0f;
            for (int nIdx = 0; nIdx < 12; nIdx++) state[i++] = 0f;
        }

        // New: 6 explicit penalty flags for Rondel actions 0-5
        // These directly tell the neural network if an action will cause a penalty.
        var actingNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        for (int act = 0; act < 6; act++)
        {
            if (game.IsInvestorTurn || game.PendingBattleDefenders.Any() || actingNs == null || actingNs.ControllerId != rlPlayerId)
            {
                state[i++] = 0f; // Not a rondel turn
                continue;
            }

            int targetSlot = (actingNs.RondelPosition.GetValueOrDefault() + act + 1) % 8;
            bool isPenalized = false;

            if (targetSlot == 1) // Factory
            {
                bool noMoney = actingNs.Treasury < 5;
                bool allBuiltOrBlocked = false;
                if (!noMoney)
                {
                    var homeCities = TerritoryData.AllTerritories
                        .Where(t => t.Nation == actingNs.Nation && t.CityType != CityType.None);
                    allBuiltOrBlocked = homeCities.All(city =>
                    {
                        var ts = _context.TerritoryStates.FirstOrDefault(t => t.GameId == gameId && t.TerritoryId == city.Id);
                        if (ts != null && ts.HasFactory) return true;
                        return _context.Units.Any(u => u.GameId == gameId && u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != actingNs.Nation && u.IsHostile);
                    });
                }
                if (noMoney || allBuiltOrBlocked) isPenalized = true;
            }
            else if (targetSlot == 5) // Import
            {
                if (actingNs.Treasury < 1) isPenalized = true;
            }
            else if (targetSlot == 3 || targetSlot == 7) // Maneuver
            {
                bool hasUnits = _context.Units.Any(u => u.GameId == gameId && u.Nation == actingNs.Nation);
                if (!hasUnits) isPenalized = true;
            }

            state[i++] = isPenalized ? 1.0f : 0.0f;
        }

        // New: 3 explicit taxation outcome flags
        if (actingNs != null)
        {
            var taxPreview = Helpers.TaxationHelper.PreviewTaxation(game, actingNs);
            state[i++] = taxPreview.ExpectedBonus / 5.0f; // Scale assuming max bonus is ~5M
            state[i++] = taxPreview.ExpectedTreasuryGain / 23.0f; // Scale assuming max tax is ~23M
            state[i++] = taxPreview.ExpectedPowerGain / 5.0f; // Scale assuming max power jump is ~5
        }
        else
        {
            state[i++] = 0f;
            state[i++] = 0f;
            state[i++] = 0f;
        }

        return state;
    }

    private bool[] GetActionMask(ApplicationDbContext _context, Guid gameId, Guid rlPlayerId)
    {
        var mask = new bool[64];
        var game = _context.Games.Include(g => g.Players).Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
        if (game == null) return mask;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return mask;

        if (game.PendingBattleDefenders.Any())
        {
            bool rlIsDefender = game.PendingBattleDefenders.Any(def =>
                game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));

            if (rlIsDefender)
            {
                _logger.LogInformation($"[RL ACTION MASK] Agent is defending a battle. Masking Fight and Retreat.");
                mask[7] = true; // Fight
                mask[8] = true; // Retreat
                return mask;
            }
        }

        if (game.IsInvestorTurn)
        {
            var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
            var bondCosts = new[] { 2, 4, 6, 9, 12, 16, 20, 25, 30 };

            mask[63] = true; // Pass option

            for (int nIdx = 0; nIdx < 6; nIdx++)
            {
                for (int cIdx = 0; cIdx < 9; cIdx++)
                {
                    var n = imperial2030Nations[nIdx];
                    var c = bondCosts[cIdx];
                    var bond = game.Bonds.FirstOrDefault(b => b.Nation == n && b.Cost == c);

                    if (bond != null && bond.HolderId == null && rlPlayer.Cash >= c)
                    {
                        mask[9 + nIdx * 9 + cIdx] = true;
                    }
                }
            }
            return mask;
        }

        var ns = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        if (ns == null || ns.ControllerId != rlPlayerId) return mask;

        int currentPos = ns.RondelPosition ?? 0;

        for (int dist = 1; dist <= 6; dist++)
        {
            int targetSlot = (currentPos + dist) % 8;
            mask[dist - 1] = IsSlotValid(ns, rlPlayer, targetSlot);
        }

        mask[6] = false; // Action 6 is unused for Rondel (moving 7 spaces is illegal in Imperial 2030)

        // Failsafe: if no actions are somehow valid, just force action 0 (move 1 space)
        if (!mask.Take(6).Any(m => m)) mask[0] = true;

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
