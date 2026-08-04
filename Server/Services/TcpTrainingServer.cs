using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services;

public class TcpTrainingServer : BackgroundService
{
    private readonly BotService _botService;
    private readonly ILogger<TcpTrainingServer> _logger;

    private static readonly Dictionary<string, TrainingSession> _sessions = new();

    public TcpTrainingServer(BotService botService, ILogger<TcpTrainingServer> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    public class TrainingSession
    {
        public Game Game { get; set; } = default!;
        public Guid RLPlayerId { get; set; }
        public string? ManeuverSelectedTerritoryId { get; set; } // Non-null when in Stage 2
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
        game.TerritoryStates = newTerritoryStates;

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

        game.NationStates = newNationStates;
        game.Bonds = newBonds;
        var bonds = newBonds;
        var nationStates = newNationStates;
        var allPlayers = players;

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

            var nsPrimary = nationStates.First(ns => ns.Nation == def.Primary);
            nsPrimary.Treasury += 9;

            var bond2M = bonds.First(b => b.Nation == def.Secondary && b.Cost == 2);
            bond2M.HolderId = player.Id;

            var nsSecondary = nationStates.First(ns => ns.Nation == def.Secondary);
            nsSecondary.Treasury += 2;
        }


        var bondsHeld = bonds.Where(b => b.HolderId != null).ToList();
        var nationStatesToUpdate = nationStates;

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
                    continue;
                }
            }

            ns.RondelPosition = null;
            if (controller != null)
            {
                ns.ControllerId = controller.Id;
            }
        }


        if (allPlayers.Any())
        {
            var sorted = allPlayers.OrderBy(p => p.Id).ToList();
            var gameToInit = game;
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
            }
        }

        var gameToUpdate = game;
        var playersToUpdate = players;

        if (gameToUpdate != null)
        {
            gameToUpdate.Status = GameStatus.InProgress;
            var firstNs = gameToUpdate.NationStates.FirstOrDefault(ns => ns.Nation == gameToUpdate.CurrentTurnNation);
            if (firstNs == null || !firstNs.ControllerId.HasValue)
            {
                gameToUpdate.AdvanceTurn();
            }
        }

        int startingCash = 13;
        foreach (var p in playersToUpdate)
        {
            p.Cash = startingCash;
            int pkgCount = distribution.Values.Count(v => v.Id == p.Id);
            p.Cash -= pkgCount * 11;
        }


        var sessionId = Guid.NewGuid().ToString();
        _sessions[sessionId] = new TrainingSession { Game = game, RLPlayerId = rlPlayer.Id };

        var state = GetStateVector(game, rlPlayer.Id);
        var mask = GetActionMask(game, _sessions[sessionId]);

        return new ResetResponse { SessionId = sessionId, State = state, ActionMask = mask };
    }

    private async Task TryPlayBotTurnAsync(Game game)
    {
        if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
        {
            var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
            if (actor != null && actor.IsBot)
            {
                try
                {
                    await _botService.BotInvestorAction(null, game, actor);
                }
                catch (Bots.Strategies.RlTrainingPauseException)
                {
                    return; // Pause loop so RL Python env can fetch state
                }
            }
        }
        else if (game.PendingBattleDefenders.Any())
        {
            try
            {
                await _botService.HandleBotBattleResponse(null, game);
            }
            catch (Bots.Strategies.RlTrainingPauseException)
            {
                return; // Pause loop so RL Python env can fetch state
            }
        }
        else if (game.PendingSwissBankForceNation != null)
        {
            var botResponders = game.PendingSwissBankResponders
                .Select(id => game.Players.FirstOrDefault(p => p.Id == id))
                .Where(p => p != null && p.IsBot)
                .ToList();

            if (botResponders.Any())
            {
                try
                {
                    await _botService.HandleBotSwissBankResponse(null, game, botResponders);
                }
                catch (Bots.Strategies.RlTrainingPauseException)
                {
                    return;
                }
            }
        }
        else
        {
            var nationState = game.NationStates.FirstOrDefault(ns => ns.Nation == game.CurrentTurnNation);
            if (nationState?.ControllerId != null)
            {
                var controller = game.Players.First(p => p.Id == nationState.ControllerId);
                if (controller != null && controller.IsBot)
                {
                    try
                    {
                        await _botService.ExecuteBotTurn(null, game, nationState, controller);
                    }
                    catch (Bots.Strategies.RlTrainingPauseException)
                    {
                        // Pause the game loop so the Training Controller can fetch the next action from Python
                        return;
                    }
                }
            }
        }
    }

    private async Task<StepResponse?> HandleStepAsync(TcpRequest req)
    {
        if (!_sessions.TryGetValue(req.SessionId, out var session)) return null;
        var game = session.Game;
        if (game == null) return null;

        var player = game.Players.First(p => p.Id == session.RLPlayerId);

        float prevVP = CalculateRelativeVP(game, session.RLPlayerId);

        // Snapshot pre-turn state to detect wasted actions
        var preNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        int? preTreasury = preNs?.Treasury;
        int? preRondelPos = preNs?.RondelPosition;
        bool wasRondelTurn = !game.IsInvestorTurn && !game.PendingBattleDefenders.Any()
                             && preNs != null && preNs.ControllerId == session.RLPlayerId;

        int preFlags = 0;
        int preHostilesInHome = 0;
        int expectedTaxBonus = 0;
        int expectedTaxRevenue = 0;
        int expectedTaxCosts = 0;
        bool isTaxationAction = false;

        if (preNs != null && preNs.ControllerId == session.RLPlayerId)
        {
            preFlags = game.TerritoryStates.Count(t => t.Controller == preNs.Nation);
            var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation).Select(t => t.Id).ToHashSet();
            preHostilesInHome = game.Units.Count(u => homeTerritories.Contains(u.TerritoryId) && u.Nation != preNs.Nation && u.IsHostile);

            if (wasRondelTurn && req.Action >= 0 && req.Action <= 5 && preRondelPos.HasValue)
            {
                int targetSlot = (preRondelPos.Value + req.Action + 1) % 8;
                if (targetSlot == 0) // Taxation slot
                {
                    isTaxationAction = true;
                    var taxPreview = Imperial2030.Server.Helpers.TaxationHelper.PreviewTaxation(game, preNs);
                    expectedTaxBonus = taxPreview.ExpectedBonus;

                    int factoryRevenue = 0;
                    var territoriesWithFactories = game.TerritoryStates.Where(ts => ts.HasFactory).ToList();
                    foreach (var ts in territoriesWithFactories)
                    {
                        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
                        if (territoryDef != null && territoryDef.Nation == preNs.Nation)
                        {
                            bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != preNs.Nation && u.IsHostile);
                            if (!hasHostileArmy) factoryRevenue += 2;
                        }
                    }
                    int flagRevenue = Math.Min(15, game.TerritoryStates.Count(ts => ts.Controller == preNs.Nation));
                    expectedTaxRevenue = Math.Min(23, factoryRevenue + flagRevenue);
                    expectedTaxCosts = game.Units.Count(u => u.Nation == preNs.Nation);
                }
            }
        }

        var preUnitCounts = game.Units.GroupBy(u => u.Nation).ToDictionary(g => g.Key, g => g.Count());
        var preFactoryCounts = game.TerritoryStates.Where(t => TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).Nation.HasValue).GroupBy(t => TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).Nation).ToDictionary(g => g.Key, g => g.Where(t => t.HasFactory).Count());
        var preOccupiedFactories = game.TerritoryStates.Where(t => t.HasFactory).ToDictionary(
            t => t.TerritoryId,
            t => game.Units.Any(u => u.TerritoryId == t.TerritoryId && u.Nation != TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == t.TerritoryId)?.Nation && u.IsHostile)
        );
        float explicitBonusReward = 0f;

        bool wasManeuverAction = false;
        if (req.Action == 63 && game.CurrentManeuverPhase != ManeuverPhase.None)
        {
            wasManeuverAction = true;
            // Pass Maneuver
            if (game.CurrentManeuverPhase == ManeuverPhase.Fleets) game.CurrentManeuverPhase = ManeuverPhase.Armies;
            else game.CurrentManeuverPhase = ManeuverPhase.None;

            session.ManeuverSelectedTerritoryId = null;
        }
        else if (req.Action >= 64 && req.Action <= 125)
        {
            wasManeuverAction = true;
            // Stage 1: Select Unit Territory
            int idx = req.Action - 64;
            if (idx >= 0 && idx < RLBotStrategy.AllManeuverTerritories.Length)
            {
                session.ManeuverSelectedTerritoryId = RLBotStrategy.AllManeuverTerritories[idx];
            }
        }
        else if (req.Action >= 126 && req.Action <= 188)
        {
            wasManeuverAction = true;
            // Stage 2: Select Destination
            var unitType = game.CurrentManeuverPhase == ManeuverPhase.Fleets ? UnitType.Fleet : UnitType.Army;
            var unit = game.Units.FirstOrDefault(u => u.TerritoryId == session.ManeuverSelectedTerritoryId && u.UnitType == unitType && u.Nation == game.CurrentTurnNation && !u.HasMoved);

            if (unit != null)
            {
                unit.HasMoved = true;
                if (req.Action != 126) // Not "Do Not Move"
                {
                    int destIdx = req.Action - 127;
                    if (destIdx >= 0 && destIdx < RLBotStrategy.AllManeuverTerritories.Length)
                    {
                        var target = RLBotStrategy.AllManeuverTerritories[destIdx];

                        var friendlyNations = game.NationStates.Where(n => n.ControllerId == session.RLPlayerId).Select(n => n.Nation).ToHashSet();
                        bool hasEnemy = game.Units.Any(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
                        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
                        bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                        var strategy = _botService.GetStrategy(player);
                        bool isHostileMove = strategy.DetermineHostility(hasEnemy, isForeignHome);

                        unit.TerritoryId = target;
                        unit.IsHostile = isHostileMove;

                        var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == target && u.UnitType == unit.UnitType && !friendlyNations.Contains(u.Nation));
                        if (enemyUnit != null && isHostileMove)
                        {
                            game.Units.Remove(unit);
                            game.Units.Remove(enemyUnit);
                        }

                        await _botService.BotTryDestroyFactories(null, game, unit.Nation, player);
                    }
                }
            }
            session.ManeuverSelectedTerritoryId = null;
        }
        else
        {
            // Base Actions (0-63)
            RLBotStrategy.TrainingActionOverride.Value = req.Action;
            _botService.SkipDelays = true;
            await TryPlayBotTurnAsync(game);
            RLBotStrategy.TrainingActionOverride.Value = null;
        }

        // Auto-advance maneuver phase logic
        if (game.CurrentManeuverPhase == ManeuverPhase.Fleets)
        {
            bool hasFleets = game.Units.Any(u => u.Nation == game.CurrentTurnNation && u.UnitType == UnitType.Fleet && !u.HasMoved);
            if (!hasFleets) game.CurrentManeuverPhase = ManeuverPhase.Armies;
        }
        if (game.CurrentManeuverPhase == ManeuverPhase.Armies)
        {
            bool hasArmies = game.Units.Any(u => u.Nation == game.CurrentTurnNation && u.UnitType == UnitType.Army && !u.HasMoved);
            if (!hasArmies) game.CurrentManeuverPhase = ManeuverPhase.None;
        }

        // If we were manually stepping through maneuver, and the maneuver phase just ended, we must advance the turn
        if (wasManeuverAction && game.CurrentManeuverPhase == ManeuverPhase.None && game.Status == GameStatus.InProgress)
        {
            var nationState = game.NationStates.First(ns => ns.Nation == game.CurrentTurnNation);
            game.AdvanceTurn();
        }

        // Calculate destruction explicit rewards before AdvanceUntilRLTurn (so we don't reward for other bots' actions)
        foreach (Nation nation in Enum.GetValues(typeof(Nation)))
        {
            int preCount = preUnitCounts.ContainsKey(nation) ? preUnitCounts[nation] : 0;
            int postCount = game.Units.Count(u => u.Nation == nation);
            if (postCount < preCount)
            {
                var rlInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId == session.RLPlayerId).Sum(b => b.Interest);
                var leaderInterest = game.Players.Select(p => game.Bonds.Where(b => b.Nation == nation && b.HolderId == p.Id).Sum(b => b.Interest)).DefaultIfEmpty(0).Max();

                if (leaderInterest >= 2 * rlInterest && leaderInterest > 0)
                {
                    explicitBonusReward += 1.0f * (preCount - postCount);
                    //_logger.LogInformation($"[RL REWARD] Destroyed unit of {nation}. +{1.0f * (preCount - postCount)}");
                }
            }
        }

        var postFactoryCounts = game.TerritoryStates.Where(t => TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).Nation.HasValue).GroupBy(t => TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).Nation).ToDictionary(g => g.Key, g => g.Where(t => t.HasFactory).Count());
        if (postFactoryCounts.Values.Sum() < preFactoryCounts.Values.Sum())
        {
            foreach (Nation nation in Enum.GetValues(typeof(Nation)))
            {
                int preCount = preFactoryCounts.ContainsKey(nation) ? preFactoryCounts[nation] : 0;
                int postCount = postFactoryCounts.ContainsKey(nation) ? postFactoryCounts[nation] : 0;
                if (postCount < preCount)
                {
                    var rlInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId == session.RLPlayerId).Sum(b => b.Interest);
                    var leaderInterest = game.Players.Select(p => game.Bonds.Where(b => b.Nation == nation && b.HolderId == p.Id).Sum(b => b.Interest)).DefaultIfEmpty(0).Max();

                    if (leaderInterest >= 2 * rlInterest && leaderInterest > 0)
                    {
                        explicitBonusReward += 3.0f * (preCount - postCount);
                        _logger.LogInformation($"[RL REWARD] Destroyed factory of {nation}. +{3.0f * (preCount - postCount)}");
                    }
                }
            }
        }

        foreach (var kvp in preOccupiedFactories)
        {
            bool wasOccupied = kvp.Value;
            var tId = kvp.Key;
            var def = TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == tId);
            if (def != null && def.Nation.HasValue)
            {
                bool isOccupiedNow = game.Units.Any(u => u.TerritoryId == tId && u.Nation != def.Nation && u.IsHostile);
                if (!wasOccupied && isOccupiedNow)
                {
                    var occupyingUnit = game.Units.FirstOrDefault(u => u.TerritoryId == tId && u.Nation != def.Nation && u.IsHostile);
                    var occupyingNs = game.NationStates.FirstOrDefault(n => n.Nation == occupyingUnit?.Nation);
                    if (occupyingNs != null && occupyingNs.ControllerId == session.RLPlayerId)
                    {
                        var nation = def.Nation.Value;
                        var rlInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId == session.RLPlayerId).Sum(b => b.Interest);
                        var leaderInterest = game.Players.Select(p => game.Bonds.Where(b => b.Nation == nation && b.HolderId == p.Id).Sum(b => b.Interest)).DefaultIfEmpty(0).Max();

                        if (leaderInterest >= 2 * rlInterest && leaderInterest > 0)
                        {
                            explicitBonusReward += 2.0f;
                            //_logger.LogInformation($"[RL REWARD] Occupied factory of {nation}. +2.0f");
                        }
                    }
                }
            }
        }

        bool done = await AdvanceUntilRLTurn(game, session.RLPlayerId);

        float newVP = CalculateRelativeVP(game, session.RLPlayerId);
        float reward = newVP - prevVP + explicitBonusReward;

        if (preNs != null && preNs.ControllerId == session.RLPlayerId)
        {
            int postFlags = game.TerritoryStates.Count(t => t.Controller == preNs.Nation);
            var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation).Select(t => t.Id).ToHashSet();
            int postHostilesInHome = game.Units.Count(u => homeTerritories.Contains(u.TerritoryId) && u.Nation != preNs.Nation && u.IsHostile);

            if (postFlags > preFlags)
            {
                reward += (postFlags - preFlags) * 1.0f; // Small reward for placing flag
                //_logger.LogInformation($"[RL REWARD] Flag placed by {preNs.Nation}. +{(postFlags - preFlags) * 1.0f}");
            }
            if (postHostilesInHome < preHostilesInHome)
            {
                reward += (preHostilesInHome - postHostilesInHome) * 5.0f; // Nice reward for clearing home territory
                //_logger.LogInformation($"[RL REWARD] Hostiles cleared from home by {preNs.Nation}. +{(preHostilesInHome - postHostilesInHome) * 5.0f}");
            }

            if (isTaxationAction)
            {
                if (expectedTaxBonus > 0)
                {
                    reward += 2.0f; // Reward if personal bonus > 0
                }
                if (expectedTaxRevenue > expectedTaxCosts)
                {
                    reward += 2.0f; // Reward if revenue > costs
                }
                else if (expectedTaxRevenue < expectedTaxCosts)
                {
                    reward -= 5.0f; // Penalty if revenue < costs
                }
            }
        }

        // Continuous reward for leading the game (or penalty for trailing)
        // This gives the agent a dense gradient to always try and increase its relative score, even if it's currently losing
        reward += newVP * 0.05f;

        // Penalty for wasted Rondel turns (e.g., picking Factory with no money)
        // Rondel slots: 0=Taxation, 1=Factory, 2=Production, 3=Maneuver, 4=Investor, 5=Import, 6=Production, 7=Maneuver
        if (wasRondelTurn && req.Action >= 0 && req.Action <= 5 && preRondelPos.HasValue)
        {
            int targetSlot = (preRondelPos.Value + req.Action + 1) % 8;
            int dist = (targetSlot - preRondelPos.Value + 8) % 8;
            int moveCost = 0;
            if (dist > 3 && preNs != null)
            {
                int pf = preNs.Power / 5;
                moveCost = (dist - 3) * (1 + pf);
            }

            // Factory (slot 1) wasted: not enough treasury OR no valid cities to build in
            if (targetSlot == 1 && preNs != null)
            {
                bool noMoney = preTreasury.HasValue && preTreasury < 5;
                bool allBuiltOrBlocked = false;
                var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation && t.CityType != CityType.None);
                if (!noMoney)
                {
                    allBuiltOrBlocked = homeCities.All(city =>
                    {
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                        if (ts != null && ts.HasFactory) return true; // Already built
                        bool blocked = game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != preNs.Nation && u.IsHostile);
                        return blocked; // Blocked by enemy
                    });
                }
                if (noMoney || allBuiltOrBlocked)
                {
                    bool allBuilt = homeCities.All(city =>
                    {
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                        return ts != null && ts.HasFactory;
                    });
                    bool blocked = !allBuilt && allBuiltOrBlocked;
                    _logger.LogWarning($"[RL PENALTY] Wasted Factory action by {preNs.Nation}. NoMoney: {noMoney}, AllBuilt: {allBuilt}, Blocked: {blocked}, Cost: {moveCost}M");
                    reward -= 15.0f;
                    reward -= allBuilt ? 10.0f : 0;
                    reward -= moveCost * 10.0f; // Extra penalty for wasting money on useless move
                }
            }
            if (targetSlot == 5 && preTreasury.HasValue && preTreasury < 1)
            {
                _logger.LogWarning($"[RL PENALTY] Wasted Import action by {preNs?.Nation}. Treasury < 1, Cost: {moveCost}M");
                reward -= 7.0f;
                reward -= moveCost * 10.0f;
            }
            // Maneuver (slot 3 or 7) with 0 units = wasted turn
            if ((targetSlot == 3 || targetSlot == 7) && preNs != null)
            {
                bool hasUnits = game.Units.Any(u => u.Nation == preNs.Nation);
                if (!hasUnits)
                {
                    if (targetSlot == 7 && dist >= 3)
                    {
                        _logger.LogWarning($"[RL PENALTY] Strategic positioning to Maneuver 2 by {preNs.Nation}. No units, but getting closer to Tax. Cost: {moveCost}M");
                        reward -= 2.0f;
                    }
                    else
                    {
                        _logger.LogWarning($"[RL PENALTY] Wasted Maneuver action by {preNs.Nation}. No units to move, Cost: {moveCost}M");
                        reward -= 10.0f;
                    }
                    reward -= moveCost * 10.0f;
                }
            }
        }

        var allScores = game.Players.Select(p => new { p.Id, Score = game.CalculateScore(p.Id) }).ToList();
        float maxOfOthersScore = allScores.Where(s => s.Id != session.RLPlayerId).Max(s => s.Score);
        float rlScore = allScores.First(s => s.Id == session.RLPlayerId).Score;

        if (game.Status == GameStatus.Finished)
        {
            _logger.LogInformation($"Finished! RL player scored {rlScore} and max of others score is {maxOfOthersScore}, intermediate score: {newVP}, reward: {reward}");

            // At the end of the game, reward perfectly aligns with the final VP difference
            reward += (rlScore - maxOfOthersScore) * 1.0f;

            // Small flat bonus for winning (and penalty for losing)
            if (rlScore > maxOfOthersScore)
            {
                reward += 100f;
            }
            else if (rlScore < maxOfOthersScore)
            {
                reward -= 100f;
            }

            _logger.LogInformation($"Final reward is {reward}, game players: {string.Join(", ", game.Players.Select(p => p.BotType))}");

            var stateResponse = GetStateVector(game, session.RLPlayerId);

            _sessions.Remove(req.SessionId);
            return new StepResponse { State = stateResponse, Reward = reward, Done = true, ActionMask = new bool[189] };
        }

        return new StepResponse { State = GetStateVector(game, session.RLPlayerId, session.ManeuverSelectedTerritoryId), Reward = reward, Done = false, ActionMask = GetActionMask(game, session) };
    }

    private async Task<bool> AdvanceUntilRLTurn(Game g, Guid rlPlayerId)
    {
        int safety = 0;
        while (safety++ < 1000)
        {
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

            await TryPlayBotTurnAsync(g);
        }
        return true;
    }

    private float CalculateRelativeVP(Game game, Guid playerId)
    {
        var allScores = game.Players.Select(p => new { p.Id, Score = CalculateVP(game, p.Id, useDense: true) }).ToList();
        float myScore = allScores.First(s => s.Id == playerId).Score;
        float maxOtherScore = allScores.Where(s => s.Id != playerId).Max(s => s.Score);
        return myScore - maxOtherScore;
    }

    private float CalculateVP(Game game, Guid playerId, bool useDense = true)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return 0;

        float score = player.Cash * .9f;

        var bonds = game.Bonds.Where(b => b.HolderId == playerId).ToList();
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
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == terrId);
                        bool isOccupied = game.Units.Any(u => u.TerritoryId == terrId && u.Nation != nation.Nation && u.IsHostile);

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

                    int flagCount = game.TerritoryStates.Count(t => t.Controller == nation.Nation);
                    int unitCount = game.Units.Count(u => u.Nation == nation.Nation);

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

    private float[] GetStateVector(Game game, Guid rlPlayerId, string? maneuverSelectedTerritoryId = null)
    {
        float[] state = new float[RLBotStrategy.StateSize];
        if (game == null) return state;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return state;

        var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
        var bonds = game.Bonds.ToList();

        var allTerritories = game.TerritoryStates.ToList();
        var allUnits = game.Units.ToList();

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
                        state[i++] = (ns.ControllerId == rlPlayerId) ? 1.0f : 0.0f; // Owned by Me
                    }
                    else
                    {
                        for (int j = 0; j < 4; j++) state[i++] = 0;
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
                state[i++] = -1.0f; // NEW: Rondel Position
                for (int j = 0; j < 63; j++) state[i++] = 0; // Bond binary parameters
                for (int j = 0; j < 16; j++) state[i++] = 0; // Territory states (4 territories * 4 floats)
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
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                        if (ts != null && ts.HasFactory) return true;
                        return game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != actingNs.Nation && u.IsHostile);
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
                bool hasUnits = game.Units.Any(u => u.Nation == actingNs.Nation);
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

        // === MAP ENCODING (2505 floats) ===
        var imperial2030NationsMap = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };

        // 1. Home Provinces (24 × 54 = 1296 floats)
        foreach (var tId in RLBotStrategy.HomeProvinceIds)
        {
            foreach (var n in imperial2030NationsMap)
            {
                var armies = allUnits.Where(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Army).ToList();
                int armyCount = armies.Count;
                EncodeUnitCount(armyCount, state, ref i);

                // Add 1 float for IsHostile presence
                bool hasHostile = armies.Any(a => a.IsHostile);
                state[i++] = hasHostile ? 1.0f : 0.0f;
            }
            foreach (var n in imperial2030NationsMap)
            {
                int fleetCount = allUnits.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Fleet);
                EncodeUnitCount(fleetCount, state, ref i);
            }
        }

        // 2. Neutral Land Territories (28 × 31 = 868 floats)
        foreach (var tId in RLBotStrategy.NeutralLandIds)
        {
            foreach (var n in imperial2030NationsMap)
            {
                int armyCount = allUnits.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Army);
                EncodeUnitCount(armyCount, state, ref i);
            }
            var ts = allTerritories.FirstOrDefault(t => t.TerritoryId == tId);
            Nation? controller = ts?.Controller;
            EncodeFlagControl(controller, imperial2030NationsMap, state, ref i);
        }

        // 3. Sea Zones (11 × 31 = 341 floats)
        foreach (var tId in RLBotStrategy.SeaZoneIds)
        {
            foreach (var n in imperial2030NationsMap)
            {
                int fleetCount = allUnits.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Fleet);
                EncodeUnitCount(fleetCount, state, ref i);
            }
            var ts = allTerritories.FirstOrDefault(t => t.TerritoryId == tId);
            Nation? controller = ts?.Controller;
            EncodeFlagControl(controller, imperial2030NationsMap, state, ref i);
        }

        // === MANEUVER CONTEXT (66 floats) ===
        // 1. Maneuver Selected Territory (63 floats: 62 territories + 1 None)
        for (int idx = 0; idx < RLBotStrategy.AllManeuverTerritories.Length; idx++)
        {
            state[i++] = (maneuverSelectedTerritoryId == RLBotStrategy.AllManeuverTerritories[idx]) ? 1.0f : 0.0f;
        }
        state[i++] = maneuverSelectedTerritoryId == null ? 1.0f : 0.0f;

        // 2. Current Maneuver Phase (3 floats: None, Fleets, Armies)
        state[i++] = game.CurrentManeuverPhase == ManeuverPhase.None ? 1.0f : 0.0f;
        state[i++] = game.CurrentManeuverPhase == ManeuverPhase.Fleets ? 1.0f : 0.0f;
        state[i++] = game.CurrentManeuverPhase == ManeuverPhase.Armies ? 1.0f : 0.0f;

        return state;
    }

    private static void EncodeUnitCount(int count, float[] state, ref int i)
    {
        state[i++] = count == 1 ? 1f : 0f;
        state[i++] = count == 2 ? 1f : 0f;
        state[i++] = count == 3 ? 1f : 0f;
        state[i++] = count >= 4 ? 1f : 0f;
    }

    private static void EncodeFlagControl(Nation? controller, Nation[] nations, float[] state, ref int i)
    {
        foreach (var n in nations)
            state[i++] = (controller.HasValue && controller.Value == n) ? 1f : 0f;
        state[i++] = !controller.HasValue ? 1f : 0f;
    }

    private bool[] GetActionMask(Game game, TrainingSession session)
    {
        var mask = new bool[189];
        var rlPlayerId = session.RLPlayerId;
        if (game == null) return mask;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return mask;

        if (game.PendingBattleDefenders.Any())
        {
            bool rlIsDefender = game.PendingBattleDefenders.Any(def =>
                game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));

            if (rlIsDefender)
            {
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

        if (game.CurrentManeuverPhase != ManeuverPhase.None)
        {
            var units = game.Units.Where(u => u.Nation == ns.Nation).ToList();
            if (session.ManeuverSelectedTerritoryId == null) // Stage 1
            {
                mask[63] = true; // Pass (End Maneuver)

                // Select valid units based on phase
                var validUnits = game.CurrentManeuverPhase == ManeuverPhase.Fleets
                    ? units.Where(u => u.UnitType == UnitType.Fleet && !u.HasMoved)
                    : units.Where(u => u.UnitType == UnitType.Army && !u.HasMoved);

                foreach (var unit in validUnits)
                {
                    int idx = Array.IndexOf(RLBotStrategy.AllManeuverTerritories, unit.TerritoryId);
                    if (idx >= 0) mask[64 + idx] = true;
                }
            }
            else // Stage 2
            {
                mask[126] = true; // Do Not Move

                // Find valid destinations
                var unitType = game.CurrentManeuverPhase == ManeuverPhase.Fleets ? UnitType.Fleet : UnitType.Army;
                var selectedUnit = units.FirstOrDefault(u => u.TerritoryId == session.ManeuverSelectedTerritoryId && u.UnitType == unitType && !u.HasMoved);

                if (selectedUnit != null)
                {
                    // Copy heuristic adjacency logic (simplified)
                    if (MapConnectivity.Adjacency.TryGetValue(selectedUnit.TerritoryId, out var neighbors))
                    {
                        var validNeighbors = neighbors.ToList();

                        // NOTE: Proper rail/convoy logic should be used here, but for RL, direct adjacency is safe start.
                        // We filter by sea/land
                        if (unitType == UnitType.Fleet)
                        {
                            validNeighbors = validNeighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)).ToList();
                        }
                        else
                        {
                            validNeighbors = validNeighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Land)).ToList();
                        }

                        foreach (var dest in validNeighbors)
                        {
                            int idx = Array.IndexOf(RLBotStrategy.AllManeuverTerritories, dest);
                            if (idx >= 0) mask[127 + idx] = true;
                        }
                    }
                }
            }
            return mask;
        }

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
