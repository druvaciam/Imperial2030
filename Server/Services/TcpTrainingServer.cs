using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imperial2030.Server.Data;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Helpers;

namespace Imperial2030.Server.Services;

public class TcpTrainingServer : BackgroundService
{
    private readonly BotService _botService;
    private readonly ILogger<TcpTrainingServer> _logger;

    // Concurrent because multiple training envs (e.g. SubprocVecEnv workers) each hold their own TCP
    // connection to this server and can Reset/Step in parallel, all hitting this dictionary concurrently.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TrainingSession> _sessions = new();

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
        public string? PendingFactoryDestructionTerritoryId { get; set; } // Non-null while awaiting a Destroy/Keep decision
        // Territories already asked about this turn (destroyed or kept). Nothing about the board changes when
        // the agent chooses Keep, so re-scanning for candidates without this would re-offer the exact same
        // territory forever — this cap ensures each qualifying stack is asked at most once per turn.
        public HashSet<string> DecidedFactoryDestructionTerritoriesThisTurn { get; set; } = new();
        /// <summary>
        /// The nation that landed on the Factory slot THIS TURN and still owes a build/skip decision, or
        /// null. Set by the rondel move, cleared when the decision is made or the turn moves on. Kept as
        /// session state on purpose: RondelPosition persists across turns and HasBuiltThisTurn is cleared
        /// every turn, so deriving "decision owed" from those two would say yes on every later turn too.
        /// </summary>
        public Nation? FactoryDecisionOwedBy { get; set; }

        public int? PendingImportRemaining { get; set; } // Non-null while stepping through an Import decision sequence

        /// <summary>
        /// The nation whose turn the Import sequence belongs to, set by the rondel move like
        /// <see cref="FactoryDecisionOwedBy"/>. The sequence is dropped if the turn moves on without it
        /// finishing (a government change in the Investor turn the move opened hands the nation to a bot).
        /// </summary>
        public Nation? ImportSequenceNation { get; set; }
        /// <summary>
        /// Units placed so far by the current step-by-step Import sequence: its count feeds the
        /// wasted-import-with-money penalty, its contents are what ImportEngine.CompleteImport logs.
        /// </summary>
        public List<(UnitType UnitType, string TerritoryId)> ImportPlacedThisSequence { get; set; } = new();

        /// <summary>
        /// Board-position baseline for the RL-controlled nation's current Maneuver. Set on arriving on
        /// either Maneuver rondel slot and cleared only after the complete phase, including battles,
        /// territory control, and factory-destruction decisions, has resolved.
        /// </summary>
        public ManeuverOutcomeSnapshot? ManeuverOutcome { get; set; }

        /// <summary>
        /// Multiplier applied to every shaping term before it is folded into the reward, set per episode
        /// by the trainer (see train.py's CurriculumCallback). 1.0 reproduces the historical behaviour, so
        /// a client that never sends it - including any older imperial_env.py - is unaffected.
        /// </summary>
        public float ShapingScale { get; set; } = 1.0f;

        /// <summary>
        /// An ADDITIONAL multiplier on the two Factory penalties only (the wasted-Factory rondel penalty
        /// and the avoidable-skip penalty), on top of <see cref="ShapingScale"/>.
        ///
        /// Exists because the Factory slot is uniquely trap-shaped. A nation has four home cities and can
        /// hold at most one factory in each (Imperial-2030-Rules.pdf p.7, "Only one factory may be built in
        /// each city"), two of which are already built at setup (p.4) - so the USUAL number of builds
        /// available across a game is two, after which landing on the slot does nothing and is penalised.
        /// Not a limit: three armies can destroy a foreign factory (p.11), which frees that city to be
        /// built in again - GetFactoryBuildOptions gates only on !HasFactory, so the engine allows the
        /// rebuild. It is rare rather than impossible (one destruction across the 260-move exported game),
        /// which is enough to make the slot mostly-dead late without ever being reliably dead.
        ///
        /// An agent that has not yet discovered what a factory pays back sees only the dead landings and
        /// learns to avoid the slot outright - measured at 0.30 factories built per nation-stint for RL-3
        /// against the 1.54 the heuristic bots manage (Tests/RLPerNationBehaviourTests). Holding this at 0
        /// early lets the payoff be discovered first, then ramps it back to 1.
        /// </summary>
        public float FactoryPenaltyScale { get; set; } = 1.0f;

        public int TotalSessionSteps { get; set; } = 0;
        public int LastTurnCount { get; set; } = -1;
        public int ConsecutiveSameTurnSteps { get; set; } = 0;
    }

    public sealed class ManeuverOutcomeSnapshot
    {
        public Nation Nation { get; set; }
        public int TurnCount { get; set; }
        public float InitialStrategicPotential { get; set; }
        public bool AnyUnitChangedTerritory { get; set; }
        public bool DirectEventOccurred { get; set; }
    }

    public class TcpRequest
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = ""; // "reset" or "step"

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("action")]
        public int Action { get; set; }

        [JsonPropertyName("botType")]
        public string? BotType { get; set; }

        [JsonPropertyName("opponents")]
        public List<string>? Opponents { get; set; }

        // Curriculum knobs, sent on "reset". Nullable on purpose: absent means "keep the default of 1.0",
        // so an older Python client that does not know about them trains exactly as it did before.
        [JsonPropertyName("shapingScale")]
        public float? ShapingScale { get; set; }

        [JsonPropertyName("factoryPenaltyScale")]
        public float? FactoryPenaltyScale { get; set; }
    }

    public class ResetResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("state")]
        public float[] State { get; set; } = Array.Empty<float>();

        [JsonPropertyName("actionMask")]
        public bool[] ActionMask { get; set; } = Array.Empty<bool>();

        // The shapes the Python env should declare. Reported rather than hardcoded on both sides, so an
        // appended state float (rule #17) cannot leave the env declaring a stale width.
        [JsonPropertyName("stateSize")]
        public int StateSize { get; set; } = RLBotStrategy.StateSize;

        [JsonPropertyName("actionSize")]
        public int ActionSize { get; set; } = RLBotStrategy.TotalActionSize;
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
            string? currentSessionId = null;
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
                        var res = await HandleResetAsync(req);
                        currentSessionId = res.SessionId;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(res));
                    }
                    else if (req.Command == "step")
                    {
                        currentSessionId = req.SessionId;
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
            finally
            {
                // If the connection drops mid-episode (crash, killed Python worker, any of the disconnects
                // this session has been debugging) before the game reaches GameStatus.Finished, the normal
                // cleanup path near the end of HandleStepAsync never runs. Without this, the orphaned
                // TrainingSession (its whole in-memory Game graph) and that game's RLBotStrategy cache
                // entries would leak for the life of the server process. TryRemove is a safe no-op if the
                // session already cleaned itself up normally (the common case).
                if (currentSessionId != null && _sessions.TryRemove(currentSessionId, out var orphanedSession))
                {
                    _botService.ClearStrategyCache(orphanedSession.Game.Players);
                    _logger.LogWarning($"[RL DIAGNOSTIC] Cleaned up orphaned training session {currentSessionId} on disconnect (game never reached Finished).");
                }
            }
            _logger.LogInformation("RL Client disconnected");
        }
    }

    private async Task<ResetResponse> HandleResetAsync(TcpRequest req)
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

        var availableBots = new[] { "Default", "Aggressive", "Friendly", "Greedy", "Random", "RL" };
        var opponentsList = req.Opponents != null && req.Opponents.Any() ? req.Opponents.ToArray() : new[] { "Random", "Default" };
        var rng = new Random();
        var players = new List<Player>();
        for (int i = 0; i < 6; i++)
        {
            var p = new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = null, IsBot = true, BotName = $"Bot {i}", Cash = 2 };
            if (i > 0)
            {
                p.BotType = opponentsList[rng.Next(opponentsList.Length)];
                p.BotName = $"{p.BotType} Bot {i}";
            }
            players.Add(p);
        }
        var rlPlayer = players[0];
        rlPlayer.BotName = string.IsNullOrEmpty(req.BotType) ? "RLAgent" : $"{req.BotType}Agent"; // We will control player 0
        rlPlayer.BotType = string.IsNullOrEmpty(req.BotType) ? "RL" : req.BotType;

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
            var sorted = allPlayers.GetOrderedPlayers().ToList();
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


        // Log the roster/setup snapshot the same way a real StartGame does. Training games are built here
        // rather than through GamesController.StartGame, so they never carried one - which made a halted
        // session's export unimportable ("missing its StartGame roster/setup metadata") and therefore
        // unreplayable, exactly when someone most wants to watch it back.
        GameLogger.LogStartGame(
            null,
            game,
            GameConstants.SystemPlayerName,
            distribution.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Id),
            game.Players.Select(pl => new Imperial2030.Shared.Models.PlayerRosterEntry
            {
                PlayerId = pl.Id,
                UserId = pl.UserId,
                IsHost = pl.IsHost,
                IsBot = pl.IsBot,
                BotName = pl.BotName,
                BotType = pl.BotType,
                DisplayName = pl.BotName ?? GameConstants.SystemPlayerName
            }).ToList());

        var sessionId = Guid.NewGuid().ToString();
        // Curriculum values arrive per episode rather than once per connection, so a schedule can move
        // while a long-lived SubprocVecEnv worker keeps the same socket open. Clamped because they come
        // off the wire: a negative scale would invert every penalty into a reward.
        _sessions[sessionId] = new TrainingSession
        {
            Game = game,
            RLPlayerId = rlPlayer.Id,
            ShapingScale = Math.Clamp(req.ShapingScale ?? 1.0f, 0f, 10f),
            FactoryPenaltyScale = Math.Clamp(req.FactoryPenaltyScale ?? 1.0f, 0f, 10f),
        };

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

        // A Maneuver snapshot is valid for exactly one nation turn. Normal completion clears it below;
        // this guard handles episode end or any defensive recovery path that moved the turn on first.
        if (session.ManeuverOutcome is { } staleManeuver
            && (game.Status != GameStatus.InProgress
                || staleManeuver.Nation != game.CurrentTurnNation
                || staleManeuver.TurnCount != game.TurnCount))
        {
            session.ManeuverOutcome = null;
        }

        session.TotalSessionSteps++;
        if (session.LastTurnCount == game.TurnCount)
        {
            session.ConsecutiveSameTurnSteps++;
        }
        else
        {
            session.LastTurnCount = game.TurnCount;
            session.ConsecutiveSameTurnSteps = 0;
        }

        // Halting throws on purpose. A session that hits either limit is a BUG whose cause is not yet
        // known, and swallowing it - ending the episode quietly so training carries on - would hide the
        // thing that needs fixing. It stays loud until somebody explains why a game ran 30x its normal
        // length. The state dumped below is what that explanation will be built from.
        var haltReason = SessionHaltReason(session.TotalSessionSteps, session.ConsecutiveSameTurnSteps);
        if (haltReason != null)
        {
            // Dump enough state to identify the CAUSE. The first occurrence logged only the step count,
            // which said a game ran long and nothing about why - no power points, no phase, no pending
            // battle. A game ends when a nation reaches 25 power (Imperial-2030-Rules.pdf p.12), so the
            // power column is the first thing to read: all six sitting low means nobody is taxing, which
            // is a very different bug from one nation stuck mid-phase.
            var powers = string.Join(", ", game.NationStates
                .OrderBy(n => n.Nation)
                .Select(n => $"{n.Nation}={n.Power}pp/{n.Treasury}M/{game.Units.Count(u => u.Nation == n.Nation)}u"));

            // Player CASH, not just nation treasuries. Those are different things and only one of them is
            // the player's: p.7 says treasuries are "held in trust" and may not be spent personally, while
            // cash buys bonds and is half the final score. Dumping only treasuries made it impossible to
            // answer "did the agent run out of money?" from a halt - it had to be reconstructed from the
            // action log afterwards, which showed the opposite of what the treasury column suggested.
            var cash = string.Join(", ", game.Players
                .OrderByDescending(pl => pl.Cash)
                .Select(pl => $"{pl.BotName ?? GameConstants.SystemPlayerName}={pl.Cash}M/{game.Bonds.Count(b => b.HolderId == pl.Id)}bonds"));

            _logger.LogError(
                $"[RL DIAGNOSTIC] Session {req.SessionId} {haltReason}. " +
                $"STATE: turn #{game.TurnCount} {game.CurrentTurnNation}, " +
                $"phase={game.CurrentManeuverPhase}, investorTurn={game.IsInvestorTurn}, " +
                $"pendingBattle={(game.PendingBattleDefenders.Any() ? $"{game.PendingBattleTerritoryId} vs {string.Join("/", game.PendingBattleDefenders)}" : "none")}, " +
                $"pendingSwissBank={game.PendingSwissBankForceNation?.ToString() ?? "none"}, " +
                $"consecutiveSameTurn={session.ConsecutiveSameTurnSteps}, factories={game.TerritoryStates.Count(t => t.HasFactory)}. " +
                $"NATIONS: {powers}. PLAYERS: {cash}");

            // The state line above is ONE FRAME. It cannot say whether power was still climbing or had
            // plateaued hundreds of turns earlier, and reading a trajectory out of a single snapshot is
            // how the first analysis of this went wrong. Export the whole game so it can be replayed and
            // the trajectory actually looked at - same GameExportDto shape ExportGame produces, so it
            // imports through the existing endpoint with no special handling.
            TryExportStuckGame(game, req.SessionId);

            throw new InvalidOperationException($"Game session {req.SessionId} {haltReason}. Halting stuck session.");
        }

        var player = game.Players.First(p => p.Id == session.RLPlayerId);
        string rlPlayerName = player.BotName ?? "Bot";

        float prevVP = CalculateRelativeVP(game, session.RLPlayerId);
        int preActionCount = game.Actions.Count;

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
        int expectedTaxTreasuryGain = 0;
        int expectedTaxPowerGain = 0;
        bool isTaxationAction = false;

        if (preNs != null && preNs.ControllerId == session.RLPlayerId)
        {
            preFlags = game.TerritoryStates.Count(t => t.Controller == preNs.Nation);
            var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation).Select(t => t.Id).ToHashSet();
            preHostilesInHome = game.Units.Count(u => homeTerritories.Contains(u.TerritoryId) && u.Nation != preNs.Nation && u.IsHostile);

            if (wasRondelTurn && req.Action >= 0 && req.Action <= 5 && preRondelPos.HasValue)
            {
                int targetSlot = (preRondelPos.Value + req.Action + 1) % RondelData.SlotCount;
                if (targetSlot == RondelData.TaxationSlot)
                {
                    isTaxationAction = true;
                    var taxPreview = Imperial2030.Server.Helpers.TaxationHelper.PreviewTaxation(game, preNs);
                    expectedTaxBonus = taxPreview.ExpectedBonus;
                    expectedTaxTreasuryGain = taxPreview.ExpectedTreasuryGain;
                    expectedTaxPowerGain = taxPreview.ExpectedPowerGain;

                    int unblockedFactories = Helpers.TaxationHelper.CountUnblockedFactories(game, preNs.Nation);
                    int flagCount = game.TerritoryStates.Count(ts => ts.Controller == preNs.Nation);
                    expectedTaxRevenue = TaxationRules.ComputeRevenue(unblockedFactories, flagCount);
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

        bool wasImportAction = false;
        bool wasFactoryBuildAction = false;
        ManeuverOutcomeSnapshot? completedManeuver = null;
        float completedManeuverPotential = 0f;

        void MarkDirectManeuverEvent(Nation actingNation)
        {
            var snapshot = completedManeuver ?? session.ManeuverOutcome;
            if (snapshot?.Nation == actingNation) snapshot.DirectEventOccurred = true;
        }

        // The flag is scoped to one turn of one nation. If the turn has moved on without it resolving
        // (a bot turn played inside AdvanceUntilRLTurn, a battle interposing, an episode reset), drop it
        // rather than let it fire on some later turn of that nation.
        if (session.FactoryDecisionOwedBy.HasValue && session.FactoryDecisionOwedBy != game.CurrentTurnNation)
        {
            session.FactoryDecisionOwedBy = null;
        }
        // Same for an Import sequence the turn moved past.
        if (session.PendingImportRemaining.HasValue && session.ImportSequenceNation != game.CurrentTurnNation)
        {
            session.PendingImportRemaining = null;
            session.ImportSequenceNation = null;
            session.ImportPlacedThisSequence = new();
        }

        var factoryBuildNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        bool isFactoryBuildPending = IsFactoryDecisionPending(session, factoryBuildNs, session.RLPlayerId);

        if (isFactoryBuildPending)
        {
            wasFactoryBuildAction = true;
            var ns = factoryBuildNs!;
            // Evaluated BEFORE the build below mutates Treasury/HasFactory — afterwards a successful build
            // has already spent the money and filled the city, so the same check would misreport what was
            // available at decision time (and would read the post-build treasury against the
            // factory-plus-import threshold, mis-tiering the penalty as well).
            float skipPenalty = AvoidableFactorySkipPenaltyFor(game, ns);
            bool built = false;

            if (req.Action >= RLBotStrategy.FactoryBuildActionBase && req.Action < RLBotStrategy.FactoryBuildActionBase + RLBotStrategy.FactoryBuildActionCount && ns.Treasury >= FactoryCost)
            {
                int slotIndex = req.Action - RLBotStrategy.FactoryBuildActionBase;
                var (orderedHome, canBuild) = GetFactoryBuildOptions(game, ns);

                if (slotIndex < orderedHome.Count && canBuild[slotIndex])
                {
                    var cityId = orderedHome[slotIndex].Id;
                    var build = FactoryEngine.BuildFactory(null, game, cityId);
                    if (!build.Ok)
                    {
                        // The mask said this slot was legal; the engine disagreeing is a bug in the mask.
                        throw new InvalidOperationException($"Training step: mask allowed a factory in {cityId} for {ns.Nation} but the engine refused: {build.Error}");
                    }

                    // Immediate reward for growing production capacity. Without this, the only reward signal for
                    // a new factory comes from the dense VP term's factoryScore, discounted by Power/5 — which is
                    // near-zero early game (when Power is low) i.e. exactly when this decision matters most. That
                    // starves the agent of any timely feedback for investing, so it tends to under-build and just
                    // cycle Taxation/Investor instead. This is deliberately unconditional (unlike the destroy/occupy
                    // rewards below, which only pay out when it visibly hurts a specific leading rival) — growth is
                    // valuable to the acting nation on its own merits, not contingent on comparing to a rival's interest.
                    // Sized to be comparable to the "wasted Factory action" penalty below (-8 base), not dwarfed by
                    // it — a +5/-15+ asymmetry taught the agent to just never visit the Factory slot at all, which
                    // showed up empirically as RL-3 building zero factories in 4 of 6 worst-loss test games while
                    // every single opponent bot built at least one in all 6 (see the RL-3 worst-loss export
                    // investigation). The goal here is a fair expected value for attempting under uncertainty, not
                    // eliminating the penalty for genuinely wasted attempts.
                    explicitBonusReward += FactoryBuildReward;
                    built = true;
                    _logger.LogInformation($"[RL REWARD] Built factory in {cityId} for {ns.Nation}. Reward: +{FactoryBuildReward}");
                }
            }

            // Landed on Factory, could build, chose not to. The skip action is always unmasked (with no
            // buildable city the mask would otherwise be empty), and the wasted-Factory penalty on the
            // rondel move only fires when the nation COULDN'T build - so this is the only cost of skipping.
            // The reduced tier applies when the treasury could not have covered a factory AND a full
            // Import: the skip may then be saving for imports rather than wasting the turn.
            if (!built && skipPenalty > 0f)
            {
                explicitBonusReward -= skipPenalty * session.FactoryPenaltyScale;
                bool savingForImport = skipPenalty == ReducedFactorySkipPenalty;
                _logger.LogWarning(
                    $"[RL PENALTY] {ns.Nation} skipped an available factory build (treasury {ns.Treasury}M" +
                    (savingForImport ? ", too little to also fund a full import" : "") +
                    $"). Penalty: -{skipPenalty}");
            }

            ns.HasBuiltThisTurn = true; // Resolved either way (built, or explicitly/implicitly skipped)
            session.FactoryDecisionOwedBy = null;
        }
        else if (IsImportSequencePending(session, game))
        {
            wasImportAction = true;
            var ns = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);

            if (req.Action >= RLBotStrategy.ImportPlaceActionBase && req.Action < RLBotStrategy.ImportPlaceActionBase + RLBotStrategy.ImportPlaceActionCount && session.PendingImportRemaining > 0)
            {
                int idx = req.Action - RLBotStrategy.ImportPlaceActionBase;
                int slotIndex = idx / 2;
                var unitType = (idx % 2 == 0) ? UnitType.Army : UnitType.Fleet;
                var (orderedHome, canArmy, canFleet) = GetImportOptions(game, ns);

                if (slotIndex < orderedHome.Count && ((unitType == UnitType.Army && canArmy[slotIndex]) || (unitType == UnitType.Fleet && canFleet[slotIndex])))
                {
                    var territoryId = orderedHome[slotIndex].Id;
                    var place = ImportEngine.PlaceOne(null, game, territoryId, unitType);
                    if (!place.Ok)
                    {
                        // The mask said this placement was legal; the engine disagreeing is a bug in the mask.
                        throw new InvalidOperationException($"Training step: mask allowed importing a {unitType} to {territoryId} for {ns.Nation} but the engine refused: {place.Error}");
                    }
                    session.PendingImportRemaining--;
                    session.ImportPlacedThisSequence.Add((unitType, territoryId));
                }

                if (session.PendingImportRemaining <= 0)
                {
                    ImportEngine.CompleteImport(null, game, session.ImportPlacedThisSequence);
                    session.PendingImportRemaining = null;
                    session.ImportSequenceNation = null;
                }
            }
            else
            {
                // Stop action, or an invalid/unrecognized action for this stage — finalize either way.
                // This is the only path that can resolve the sequence with nothing placed (the "ran out of
                // remaining slots" branch above can only be reached right after a successful placement), and
                // the sequence only ever starts when treasury >= 1 (see where PendingImportRemaining is set),
                // so reaching here with zero placed unambiguously means affordable Import got wasted entirely.
                if (session.ImportPlacedThisSequence.Count == 0)
                {
                    _logger.LogWarning($"[RL PENALTY] Wasted Import action by {ns.Nation}. Had money but imported 0 units.");
                    explicitBonusReward -= 7.0f;
                }
                ImportEngine.CompleteImport(null, game, session.ImportPlacedThisSequence);
                session.PendingImportRemaining = null;
                session.ImportSequenceNation = null;
            }
        }
        else if (session.PendingFactoryDestructionTerritoryId != null && !game.IsInvestorTurn)
        {
            // Resolve a pending Destroy/Keep decision for a factory currently held under siege
            var pendingTerritoryId = session.PendingFactoryDestructionTerritoryId;
            session.PendingFactoryDestructionTerritoryId = null;
            session.DecidedFactoryDestructionTerritoriesThisTurn.Add(pendingTerritoryId);

            if (req.Action == RLBotStrategy.FactoryDestroyAction)
            {
                _botService.ExecuteFactoryDestruction(null, game, pendingTerritoryId, game.CurrentTurnNation, player);
            }

            // More stacks may still be awaiting a decision (e.g. multiple sieges resolved in the same move).
            // Excludes anything already decided this turn — Keep doesn't change the board, so the same
            // territory would otherwise keep re-qualifying and get re-offered forever.
            session.PendingFactoryDestructionTerritoryId = _botService
                .FindFactoryDestructionCandidates(game, game.CurrentTurnNation, player)
                .FirstOrDefault(t => !session.DecidedFactoryDestructionTerritoriesThisTurn.Contains(t));
        }
        // Action 63 is both "pass investment" and "end maneuver phase"; with an Investor turn open it is
        // the former and belongs to the base-actions branch below.
        else if (req.Action == 63 && game.CurrentManeuverPhase != ManeuverPhase.None && !game.IsInvestorTurn)
        {
            // Pass Maneuver
            if (game.CurrentManeuverPhase == ManeuverPhase.Fleets) game.CurrentManeuverPhase = ManeuverPhase.Armies;
            else game.CurrentManeuverPhase = ManeuverPhase.None;

            session.ManeuverSelectedTerritoryId = null;
        }
        else if (req.Action >= 64 && req.Action <= 125 && !game.IsInvestorTurn)
        {
            // Stage 1: Select Unit Territory
            int idx = req.Action - 64;
            if (idx >= 0 && idx < RLBotStrategy.AllManeuverTerritories.Length)
            {
                session.ManeuverSelectedTerritoryId = RLBotStrategy.AllManeuverTerritories[idx];
            }
        }
        else if (req.Action >= 126 && req.Action <= 188 && !game.IsInvestorTurn)
        {
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

                        if (!string.Equals(unit.TerritoryId, target, StringComparison.OrdinalIgnoreCase)
                            && session.ManeuverOutcome?.Nation == unit.Nation)
                        {
                            session.ManeuverOutcome.AnyUnitChangedTerritory = true;
                        }

                        var friendlyNations = game.NationStates.Where(n => n.ControllerId == session.RLPlayerId).Select(n => n.Nation).ToHashSet();
                        bool hasEnemy = game.Units.Any(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
                        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
                        bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                        var strategy = _botService.GetStrategy(player);
                        bool isHostileMove = strategy.DetermineHostility(hasEnemy, isForeignHome);

                        if (isHostileMove && def != null && def.Nation.HasValue && def.Nation.Value != unit.Nation)
                        {
                            var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == target);
                            if (tState != null && tState.HasFactory)
                            {
                                var defenderNation = def.Nation.Value;
                                var defenderFactoryCount = game.TerritoryStates.Count(s =>
                                {
                                    if (!s.HasFactory) return false;
                                    var t = TerritoryData.AllTerritories.FirstOrDefault(td => td.Id == s.TerritoryId);
                                    if (t == null || t.Nation != defenderNation) return false;
                                    bool isOccupied = game.Units.Any(u => u.TerritoryId == s.TerritoryId && u.Nation != defenderNation && u.IsHostile);
                                    return !isOccupied;
                                });
                                bool isTargetOccupied = game.Units.Any(u => u.TerritoryId == target && u.Nation != defenderNation && u.IsHostile);
                                if (defenderFactoryCount <= 1 && !isTargetOccupied)
                                {
                                    isHostileMove = false;
                                }
                            }
                        }

                        unit.TerritoryId = target;
                        unit.IsHostile = isHostileMove;

                        if (unitType == UnitType.Army)
                        {
                            var destinations = Imperial2030.Server.Helpers.ManeuverHelper.GetAllReachableArmyDestinations(game, session.ManeuverSelectedTerritoryId, unit.Nation);
                            var destInfo = destinations.FirstOrDefault(d => d.TerritoryId == target);
                            if (destInfo == null)
                            {
                                // The chosen destination is not in the freshly recomputed reachable set - root
                                // cause not confirmed. Logged at Error: the move above already happened, and
                                // skipping the convoy bookkeeping here leaves a fleet that convoyed this army
                                // unmarked (HasConvoyed), free to convoy a second army this turn - a rules
                                // deviation, not a cosmetic gap.
                                _logger.LogError($"[RL DIAGNOSTIC] Maneuver destination '{target}' for {unit.Nation} army not found among {destinations.Count} reachable destinations from '{session.ManeuverSelectedTerritoryId}' ({string.Join(", ", destinations.Select(d => d.TerritoryId))}).");
                            }
                            else if (destInfo.IsConvoy && destInfo.ConvoyFleets != null)
                            {
                                foreach (var f in destInfo.ConvoyFleets) f.HasConvoyed = true;
                            }
                        }

                        if (hasEnemy)
                        {
                            var foreignDefenders = game.Units
                                .Where(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation))
                                .Where(u => u.UnitType == unit.UnitType || (isForeignHome && def != null && u.Nation == def.Nation.Value && isHostileMove))
                                .Select(u => u.Nation)
                                .Distinct()
                                .ToList();

                            if (foreignDefenders.Any())
                            {
                                if (isHostileMove && foreignDefenders.Count == 1)
                                {
                                    var targetNation = foreignDefenders.First();
                                    var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == target && u.Nation == targetNation &&
                                        (u.UnitType == unit.UnitType || (isForeignHome && def != null && u.Nation == def.Nation.Value)));

                                    if (enemyUnit != null)
                                    {
                                        game.Units.Remove(unit);
                                        game.Units.Remove(enemyUnit);
                                    }
                                }
                                else
                                {
                                    game.PendingBattleTerritoryId = target;
                                    game.PendingBattleAggressorNation = unit.Nation;
                                    game.PendingBattleAggressorUnitId = unit.Id;
                                    game.PendingBattleDefenders = foreignDefenders.ToList();
                                }
                            }
                        }

                        if (!game.PendingBattleDefenders.Any())
                        {
                            session.PendingFactoryDestructionTerritoryId = _botService
                                .FindFactoryDestructionCandidates(game, unit.Nation, player)
                                .FirstOrDefault(t => !session.DecidedFactoryDestructionTerritoriesThisTurn.Contains(t));
                        }
                    }
                }
            }
            session.ManeuverSelectedTerritoryId = null;
        }
        else
        {
            // Base Actions (0-63)
            bool isInvestorTurn = game.IsInvestorTurn;
            var oldMask = isInvestorTurn ? GetActionMask(game, session) : null;

            RLBotStrategy.TrainingActionOverride.Value = req.Action;
            _botService.SkipDelays = true;
            await TryPlayBotTurnAsync(game);
            RLBotStrategy.TrainingActionOverride.Value = null;

            // BotService initializes the actual rondel phase before its RL training early-return. At that
            // point no maneuver unit has moved yet, so this is the whole-phase positional baseline. This
            // also covers a Maneuver reached after an Investor pass-through finishes.
            var maneuverNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
            if (game.CurrentManeuverPhase != ManeuverPhase.None
                && maneuverNs?.ControllerId == session.RLPlayerId
                && maneuverNs.RondelPosition is int maneuverSlot)
            {
                TryArmManeuverOutcome(session, game, maneuverNs.Nation, maneuverSlot);
            }

            // A rondel move that landed on Factory owes a build/skip decision on the NEXT step, for this
            // turn only (see IsFactoryDecisionPending). Set only on the step that made the move: a later
            // investor step of the same turn must not reset a sequence that is waiting for the phase to clear.
            var postFactoryNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
            if (!isInvestorTurn && postFactoryNs != null && postFactoryNs.ControllerId == session.RLPlayerId
                && postFactoryNs.RondelPosition == RondelData.FactorySlot && !postFactoryNs.HasBuiltThisTurn)
            {
                session.FactoryDecisionOwedBy = postFactoryNs.Nation;
            }

            // A rondel move that landed on Import starts the step-by-step Import decision sequence
            // (BotService.BotImport is a no-op for RL during training; see its early return).
            var postMoveNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
            if (!isInvestorTurn && postMoveNs != null && postMoveNs.ControllerId == session.RLPlayerId && postMoveNs.RondelPosition == RondelData.ImportSlot && !postMoveNs.HasImportedThisTurn)
            {
                wasImportAction = true;
                if (postMoveNs.Treasury >= GameConstants.ImportUnitCost)
                {
                    session.PendingImportRemaining = Math.Min(RLBotStrategy.MaxImportUnits, postMoveNs.Treasury);
                    session.ImportSequenceNation = postMoveNs.Nation;
                    session.ImportPlacedThisSequence = new();
                }
                else
                {
                    // Nothing to import; nothing to decide. Logged so the game log says why the turn did nothing.
                    GameLogger.LogImportNotAfforded(null, game, postMoveNs.Nation, player.BotName ?? "Bot");
                    postMoveNs.HasImportedThisTurn = true;
                }
            }

            if (isInvestorTurn && oldMask != null)
            {
                // A nation contributes an investment signal once it is a credible growth bet
                // (Power > InvestmentRampStart), ramping to full strength at Power >= InvestmentRampEnd.
                // A hard cutoff at 15 would only ever reward buying a nation's expensive late bonds; the ramp
                // rewards buying its cheap early ones while they are still available.
                const int InvestmentRampStart = 5;
                const int InvestmentRampEnd = 15;
                float BondInvestmentWeight(int power)
                {
                    if (power <= InvestmentRampStart) return 0f;
                    if (power >= InvestmentRampEnd) return 1f;
                    return (power - InvestmentRampStart) / (float)(InvestmentRampEnd - InvestmentRampStart);
                }

                bool passed = req.Action == 63;
                float bestAffordableWeight = 0f;
                float boughtWeight = 0f;

                var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
                for (int i = 9; i <= 62; i++)
                {
                    if (oldMask[i])
                    {
                        int bondIdx = i - 9;
                        int nationIdx = bondIdx / 9;
                        var n = imperial2030Nations[nationIdx];
                        var candidateNs = game.NationStates.First(x => x.Nation == n);
                        float weight = BondInvestmentWeight(candidateNs.Power);

                        if (weight > bestAffordableWeight) bestAffordableWeight = weight;
                        if (req.Action == i) boughtWeight = weight;
                    }
                }

                if (passed && bestAffordableWeight > 0f)
                {
                    float penalty = -50.0f * bestAffordableWeight;
                    explicitBonusReward += penalty;
                    _logger.LogWarning($"[RL PENALTY] Passed on investment with a bond available in a growing nation (weight {bestAffordableWeight:F2}). Penalty: {penalty:F1}");
                }
                else if (!passed && boughtWeight > 0f)
                {
                    float bonus = 20.0f * boughtWeight;
                    explicitBonusReward += bonus;
                    _logger.LogInformation($"[RL REWARD] Invested in a growing nation (weight {boughtWeight:F2}). Reward: +{bonus:F1}");
                }
            }
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

        // Stage 1 (explicit "which unit" selection, actions 64-125) is no longer used for training: which unit
        // moves next barely matters strategically and asking for it doubled the number of steps per maneuver
        // phase. Auto-pick the next unmoved unit here instead, and go straight to asking for its destination.
        // The 64-125 action range and its dispatch/mask handling are left in place for backward compatibility
        // (inference never used Stage 1 either — BotManeuver iterates units directly).
        if (game.CurrentManeuverPhase != ManeuverPhase.None && session.ManeuverSelectedTerritoryId == null)
        {
            var autoSelectUnitType = game.CurrentManeuverPhase == ManeuverPhase.Fleets ? UnitType.Fleet : UnitType.Army;
            var nextUnit = game.Units.FirstOrDefault(u => u.Nation == game.CurrentTurnNation && u.UnitType == autoSelectUnitType && !u.HasMoved);
            if (nextUnit != null)
            {
                session.ManeuverSelectedTerritoryId = nextUnit.TerritoryId;
            }
        }

        // Finalize only after the WHOLE Maneuver is resolved. A last-unit move can leave a battle or a
        // factory decision pending even though CurrentManeuverPhase is already None; those continuations
        // belong to this same Maneuver and must affect its post-position and direct-event record.
        if (game.Status == GameStatus.InProgress && IsManeuverOutcomeReady(session, game))
        {
            await _botService.BotUpdateTerritoryControl(null, game, player.BotName ?? "Bot");
            var snapshot = session.ManeuverOutcome!;
            completedManeuver = snapshot;
            var completedNationState = game.NationStates.First(state => state.Nation == snapshot.Nation);
            completedManeuverPotential = CalculateManeuverStrategicPotential(
                game,
                snapshot.Nation,
                completedNationState.ControllerId);
            EndTrainingTurn(game);
            session.DecidedFactoryDestructionTerritoriesThisTurn.Clear();
        }

        // Same for the step-by-step Import decision sequence, once it's fully resolved
        if (wasImportAction && !session.PendingImportRemaining.HasValue && MayRondelActionEndTheTurn(game))
        {
            EndTrainingTurn(game);
        }

        // Same for the Factory build decision, which always resolves in a single step
        if (wasFactoryBuildAction && MayRondelActionEndTheTurn(game))
        {
            EndTrainingTurn(game);
        }

        // Calculate destruction explicit rewards before AdvanceUntilRLTurn (so we don't reward for other bots' actions)
        foreach (Nation nation in Enum.GetValues(typeof(Nation)))
        {
            int preCount = preUnitCounts.ContainsKey(nation) ? preUnitCounts[nation] : 0;
            int postCount = game.Units.Count(u => u.Nation == nation);
            if (postCount < preCount)
            {
                var maneuverSnapshot = completedManeuver ?? session.ManeuverOutcome;
                var destroyedNationState = game.NationStates.FirstOrDefault(state => state.Nation == nation);
                if (maneuverSnapshot != null
                    && nation != maneuverSnapshot.Nation
                    && destroyedNationState?.ControllerId != session.RLPlayerId)
                {
                    MarkDirectManeuverEvent(maneuverSnapshot.Nation);
                }

                var rlInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId == session.RLPlayerId).Sum(b => b.Interest);
                var leaderInterest = game.Players.Select(p => game.Bonds.Where(b => b.Nation == nation && b.HolderId == p.Id).Sum(b => b.Interest)).DefaultIfEmpty(0).Max();

                if (leaderInterest >= 2 * rlInterest && leaderInterest > 0)
                {
                    explicitBonusReward += EnemyUnitDestroyedReward * (preCount - postCount);
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
                    var maneuverSnapshot = completedManeuver ?? session.ManeuverOutcome;
                    if (maneuverSnapshot != null && nation != maneuverSnapshot.Nation)
                    {
                        MarkDirectManeuverEvent(maneuverSnapshot.Nation);
                    }

                    var rlInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId == session.RLPlayerId).Sum(b => b.Interest);
                    var leaderInterest = game.Players.Select(p => game.Bonds.Where(b => b.Nation == nation && b.HolderId == p.Id).Sum(b => b.Interest)).DefaultIfEmpty(0).Max();

                    if (leaderInterest >= 2 * rlInterest && leaderInterest > 0)
                    {
                        explicitBonusReward += EnemyFactoryDestroyedReward * (preCount - postCount);
                        _logger.LogInformation($"[RL REWARD] Destroyed factory of {nation}. +{EnemyFactoryDestroyedReward * (preCount - postCount)}");
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
                            explicitBonusReward += EnemyFactoryOccupiedReward;
                            //_logger.LogInformation($"[RL REWARD] Occupied factory of {nation}. +2.0f");
                        }

                        MarkDirectManeuverEvent(occupyingNs.Nation);
                    }
                }
            }
        }

        bool done = await AdvanceUntilRLTurn(game, session.RLPlayerId);

        float newVP = CalculateRelativeVP(game, session.RLPlayerId);
        // Shaping is accumulated in explicitBonusReward and folded in ONCE, at the single point below,
        // after every shaping term has been computed - terms added after that point would be lost.
        float reward = newVP - prevVP;

        if (preNs != null && preNs.ControllerId == session.RLPlayerId)
        {
            int postFlags = game.TerritoryStates.Count(t => t.Controller == preNs.Nation);
            var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation).Select(t => t.Id).ToHashSet();
            int postHostilesInHome = game.Units.Count(u => homeTerritories.Contains(u.TerritoryId) && u.Nation != preNs.Nation && u.IsHostile);

            if (postFlags > preFlags)
            {
                explicitBonusReward += (postFlags - preFlags) * FlagPlacementReward;
                MarkDirectManeuverEvent(preNs.Nation);
                //_logger.LogInformation($"[RL REWARD] Flag placed by {preNs.Nation}. +{(postFlags - preFlags) * 1.0f}");
            }
            if (postHostilesInHome < preHostilesInHome)
            {
                explicitBonusReward += (preHostilesInHome - postHostilesInHome) * HomeReliefReward;
                MarkDirectManeuverEvent(preNs.Nation);
                //_logger.LogInformation($"[RL REWARD] Hostiles cleared from home by {preNs.Nation}. +{(preHostilesInHome - postHostilesInHome) * 5.0f}");
            }

            if (isTaxationAction)
            {
                if (expectedTaxBonus > 0)
                {
                    explicitBonusReward += 2.0f; // Reward if personal bonus > 0
                }
                if (expectedTaxRevenue > expectedTaxCosts)
                {
                    explicitBonusReward += 2.0f; // Reward if revenue > costs
                }
                if (expectedTaxPowerGain > 5)
                {
                    explicitBonusReward += 5.0f; // Reward if power gain > 5
                }

                if (expectedTaxTreasuryGain <= 0 && expectedTaxPowerGain == 0)
                {
                    explicitBonusReward -= 5.0f; // Penalty for a fully wasted Taxation turn: no treasury gain, no power gain
                }
            }
        }

        // Apply Investor penalties based on recorded actions
        for (int i = preActionCount; i < game.Actions.Count; i++)
        {
            var action = game.Actions.ElementAt(i);
            if (action.ActionType == "Investor" && action.PlayerName == rlPlayerName && !string.IsNullOrEmpty(action.Metadata))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<Imperial2030.Shared.Models.InvestorMetadata>(action.Metadata, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (meta != null)
                    {
                        if (meta.PersonalContribution > 0)
                        {
                            float penalty = MathF.Min(80.0f, MathF.Max(25.0f, meta.PersonalContribution.Value * 10.0f));
                            explicitBonusReward -= penalty; // Heavy penalty for paying out of pocket
                            _logger.LogWarning($"[RL PENALTY] {rlPlayerName} personally contributed {meta.PersonalContribution}M to interest. Penalty: -{penalty}");
                        }
                        if (meta.MissedInterest == true)
                        {
                            explicitBonusReward -= 20.0f; // Heavy penalty for missing own interest
                            _logger.LogWarning($"[RL PENALTY] {rlPlayerName} missed interest payment due to empty treasury. Penalty: -20");
                        }
                    }
                }
                catch { }
            }
        }

        // Continuous reward for leading the game (or penalty for trailing)
        // This gives the agent a dense gradient to always try and increase its relative score, even if it's currently losing
        reward += newVP * 0.05f;

        // Penalty for wasted Rondel turns (e.g., picking Factory with no money)
        // Rondel slots: 0=Taxation, 1=Factory, 2=Production, 3=Maneuver, 4=Investor, 5=Import, 6=Production, 7=Maneuver
        if (wasRondelTurn && req.Action >= 0 && req.Action <= 5 && preRondelPos.HasValue)
        {
            int targetSlot = (preRondelPos.Value + req.Action + 1) % RondelData.SlotCount;
            int dist = RondelData.GetMoveDistance(preRondelPos.Value, targetSlot);
            int moveCost = preNs == null ? 0 : RondelData.GetMoveCost(preRondelPos, targetSlot, preNs.Power);

            // Heavy penalty for paying for a long move to first Prod/Man when the second one was closer
            if (dist >= 5 && (targetSlot == RondelData.ProductionSlot1 || targetSlot == RondelData.ManeuverSlot1))
            {
                string targetName = targetSlot == RondelData.ProductionSlot1 ? "Production" : "Maneuver";
                _logger.LogWarning($"[RL PENALTY] {preNs?.Nation} paid for long move ({dist} steps) to {targetName} 1, skipping a closer {targetName} 2. Cost: {moveCost}M");
                explicitBonusReward -= 40.0f; // Heavy penalty
            }

            // Factory (slot 1) wasted: not enough treasury OR no valid cities to build in
            if (targetSlot == RondelData.FactorySlot && preNs != null)
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
                    // Halved from -15/-10 (was up to -25 before any moveCost) so a single wasted attempt doesn't
                    // outweigh two-plus successful builds at +10 above — the prior magnitude taught the agent to
                    // just avoid the Factory slot outright rather than learn when it's actually worth the risk.
                    // moveCost's own penalty is untouched: that reflects real in-game money lost on the move, not
                    // an RL-specific shaping choice, so it stays proportional to the actual waste.
                    // Scaled by the session curriculum (see TrainingSession.FactoryPenaltyScale): early in a
                    // run this is held at 0 so the agent can discover what a factory pays back before it
                    // learns to fear the slot. Both halves scale together - they punish the same decision.
                    explicitBonusReward -= (WastedFactoryActionPenalty + (allBuilt ? AllFactoriesBuiltPenalty : 0f))
                                           * session.FactoryPenaltyScale;
                    explicitBonusReward -= moveCost * 10.0f; // Extra penalty for wasting money on useless move
                }
            }
            if (targetSlot == RondelData.ImportSlot && preTreasury.HasValue && preTreasury < 1)
            {
                _logger.LogWarning($"[RL PENALTY] Wasted Import action by {preNs?.Nation}. Treasury < 1, Cost: {moveCost}M");
                explicitBonusReward -= 7.0f;
                explicitBonusReward -= moveCost * 10.0f;
            }
            // Production (slot 2 or 6) wasted: no existing factory can currently produce. Note that
            // blockade alone can NEVER fully explain this: the game engine forbids hostile entry into a
            // nation's last unoccupied factory (ManeuverController's "Cannot enter the last unoccupied
            // factory hostilely" check), so at least one factory always stays unblockaded whenever the
            // nation has any factory at all. So a fully wasted turn always requires that every currently
            // unblockaded factory's unit type (army/fleet) is already at the nation's max cap — blockade
            // can only ever narrow which *other* factories are unavailable when 2+ exist, never eliminate
            // the last one. Production itself is free, so this isn't about affordability.
            if (RondelData.IsProductionSlot(targetSlot) && preNs != null)
            {
                var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == preNs.Nation);
                int currentArmies = game.Units.Count(u => u.Nation == preNs.Nation && u.UnitType == UnitType.Army);
                int currentFleets = game.Units.Count(u => u.Nation == preNs.Nation && u.UnitType == UnitType.Fleet);
                int maxArmies = NationData.GetMaxArmies(preNs.Nation);
                int maxFleets = NationData.GetMaxFleets(preNs.Nation);

                bool canProduceAnything = homeCities.Any(city =>
                {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                    if (ts == null || !ts.HasFactory) return false;
                    bool isBlockaded = game.Units.Any(u => u.TerritoryId == city.Id && u.Nation != preNs.Nation && u.UnitType == UnitType.Army && u.IsHostile);
                    if (isBlockaded) return false;
                    return city.CityType == CityType.LightBlue ? currentFleets < maxFleets : currentArmies < maxArmies;
                });

                if (!canProduceAnything)
                {
                    _logger.LogWarning($"[RL PENALTY] Wasted Production action by {preNs.Nation}. No factory can produce (blockaded or at max unit cap). Cost: {moveCost}M");
                    explicitBonusReward -= 10.0f;
                    explicitBonusReward -= moveCost * 10.0f;
                }
            }
            // Maneuver (slot 3 or 7) with 0 units = wasted turn
            if (RondelData.IsManeuverSlot(targetSlot) && preNs != null)
            {
                bool hasUnits = game.Units.Any(u => u.Nation == preNs.Nation);
                if (!hasUnits)
                {
                    if (targetSlot == RondelData.ManeuverSlot2 && dist >= 3)
                    {
                        _logger.LogWarning($"[RL PENALTY] Strategic positioning to Maneuver 2 by {preNs.Nation}. No units, but getting closer to Tax. Cost: {moveCost}M");
                        explicitBonusReward -= 2.0f;
                    }
                    else
                    {
                        _logger.LogWarning($"[RL PENALTY] Wasted Maneuver action by {preNs.Nation}. No units to move, Cost: {moveCost}M");
                        explicitBonusReward -= 10.0f;
                    }
                    explicitBonusReward -= moveCost * 10.0f;
                }
            }
        }

        if (completedManeuver != null)
        {
            float maneuverOutcomeReward = CompleteManeuverOutcome(session, completedManeuverPotential);
            explicitBonusReward += maneuverOutcomeReward;
            if (maneuverOutcomeReward > 0f)
            {
                _logger.LogInformation(
                    $"[RL REWARD] {completedManeuver.Nation} completed Maneuver with a better strategic position. " +
                    $"Potential: {completedManeuver.InitialStrategicPotential:F2} -> {completedManeuverPotential:F2}. " +
                    $"Reward: +{maneuverOutcomeReward:F2}");
            }
            else if (maneuverOutcomeReward < 0f)
            {
                _logger.LogWarning(
                    $"[RL PENALTY] {completedManeuver.Nation} completed Maneuver with no useful net result. " +
                    $"Potential: {completedManeuver.InitialStrategicPotential:F2} -> {completedManeuverPotential:F2}. " +
                    $"Penalty: {maneuverOutcomeReward:F2}");
            }
        }

        // The single fold point. Everything above this line is shaping; everything below (the final VP
        // margin and the flat win/loss bonus) is the actual objective and is deliberately NOT scaled -
        // decaying shaping must make the terminal signal relatively STRONGER, not weaker.
        reward += explicitBonusReward * session.ShapingScale;

        var allScores = game.Players.Select(p => new { p.Id, Score = game.CalculateScore(p.Id) }).ToList();
        float maxOfOthersScore = allScores.Where(s => s.Id != session.RLPlayerId).Max(s => s.Score);
        float rlScore = allScores.First(s => s.Id == session.RLPlayerId).Score;

        if (game.Status == GameStatus.Finished)
        {
            var winner = allScores.OrderByDescending(s => s.Score).First();
            var winnerPlayer = game.Players.First(p => p.Id == winner.Id);
            string winnerName = winnerPlayer.Id == session.RLPlayerId ? $"{winnerPlayer.BotName ?? "RL"} (RL)" : (winnerPlayer.BotName ?? "Bot");

            _logger.LogInformation($"Finished! Winner: {winnerName} (score {winner.Score}). RL player scored {rlScore} and max of others score is {maxOfOthersScore}, intermediate score: {newVP}, reward: {reward}");

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

            session.ManeuverOutcome = null;
            _sessions.TryRemove(req.SessionId, out _);
            _botService.ClearStrategyCache(game.Players);
            return new StepResponse { State = stateResponse, Reward = reward, Done = true, ActionMask = new bool[RLBotStrategy.TotalActionSize] };
        }

        return new StepResponse { State = GetStateVector(game, session.RLPlayerId, session.ManeuverSelectedTerritoryId), Reward = reward, Done = false, ActionMask = GetActionMask(game, session) };
    }

    /// <summary>
    /// Ends the RL agent's turn through the engine. A rejection means the step handler's own "the turn is
    /// finished" condition disagrees with the engine's - a bug to surface, not to advance past.
    /// </summary>
    private static void EndTrainingTurn(Game game)
    {
        var result = TurnEngine.EndTurn(null, game);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"Training step tried to end {game.CurrentTurnNation}'s turn but the engine refused: {result.Error}");
        }
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

            try
            {
                await TryPlayBotTurnAsync(g);
            }
            catch (Bots.Strategies.RlTrainingPauseException) { throw; }
            catch (Exception ex)
            {
                // Every call here is for an OPPONENT bot's decision, never the RL agent's own (the loop
                // only reaches this line when the "it's the RL player's turn" exit checks above are false).
                // Log rich context before it propagates and kills this TCP session/SubprocVecEnv worker, so
                // an exact repro (bot type, nation, decision point) is available on the next occurrence
                // instead of just a stack trace pointing at what may be a JIT-collapsed frame.
                var actingNs = g.NationStates.FirstOrDefault(n => n.Nation == g.CurrentTurnNation);
                var actingPlayer = g.Players.FirstOrDefault(p => p.Id == (g.ActingPlayerId ?? actingNs?.ControllerId));
                _logger.LogError(ex, $"[RL DIAGNOSTIC] Opponent bot turn resolution threw. Nation={g.CurrentTurnNation}, ActingPlayerId={g.ActingPlayerId}, BotName={actingPlayer?.BotName}, BotType={actingPlayer?.BotType}, IsInvestorTurn={g.IsInvestorTurn}, PendingBattle={g.PendingBattleDefenders.Any()}, PendingSwissBankForce={g.PendingSwissBankForceNation}");
                throw;
            }
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
                    int unoccupiedFactoryCount = 0;

                    foreach (var terrId in homeTerritories)
                    {
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == terrId);
                        bool isOccupied = game.Units.Any(u => u.TerritoryId == terrId && u.Nation != nation.Nation && u.IsHostile);

                        if (ts != null && ts.HasFactory)
                        {
                            if (isOccupied) factoryScore += 0.02f; // Suppressed factory
                            else { factoryScore += 0.2f; unoccupiedFactoryCount++; } // Healthy factory
                        }
                        else
                        {
                            if (isOccupied) factoryScore -= 0.1f; // Enemy blocking factory building
                        }
                    }

                    int flagCount = game.TerritoryStates.Count(t => t.Controller == nation.Nation);
                    int unitCount = game.Units.Count(u => u.Nation == nation.Nation);
                    // Only reward units up to what the nation's economy can actually sustain (mirrors the
                    // taxation revenue formula: 2M/unoccupied factory + 1M/flag), hard-capped at the nation's
                    // real maximum unit count (armies + fleets, always 16 per NationData). Without this,
                    // raw unit count was a free, uncapped reward for stockpiling idle units regardless of
                    // whether they were ever used (real CalculateScore doesn't count units at all).
                    int maxUnitCount = NationData.GetMaxArmies(nation.Nation) + NationData.GetMaxFleets(nation.Nation);
                    int sustainableUnitCapacity = Math.Min(maxUnitCount, unoccupiedFactoryCount * 2 + flagCount);
                    int usefulUnitCount = Math.Min(unitCount, sustainableUnitCapacity);

                    float denseFactor = (nation.Power / 5.0f);

                    // Floor denseFactor at the bond's own breakeven point against what it cost (matching the
                    // Cash * 0.9f weight above), so a freshly bought bond in a low-Power nation the agent
                    // doesn't control registers as roughly reward-neutral instead of a large instant loss (cash
                    // spent now, ~0 credited back because Power/5 rounds to 0 below Power 5). Without this, the
                    // dense per-step shaping actively taught the agent that investing was bad, regardless of
                    // whether it was actually a good trade — see the RL-3 worst-loss export investigation. Once
                    // the nation's real Power/5 factor grows past this floor, it takes over as normal.
                    if (bond.Interest > 0)
                    {
                        float purchaseBreakevenFactor = (bond.Cost * 0.9f) / bond.Interest;
                        denseFactor = Math.Max(denseFactor, purchaseBreakevenFactor);
                    }

                    if (nation.ControllerId == playerId)
                    {
                        float flagValue = 0.02f;
                        int distanceToTax = (RondelData.SlotCount - (nation.RondelPosition ?? 0)) % RondelData.SlotCount;
                        if (distanceToTax == 0) distanceToTax = RondelData.SlotCount; // If currently on Taxation, it's 8 steps away
                        if (distanceToTax <= RondelData.FreeMoveDistance) flagValue = 0.04f; // More valuable when close to tax

                        denseFactor += factoryScore
                                     + (flagCount * flagValue)
                                     + (usefulUnitCount * 0.01f)
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
                state[i++] = ns.RondelPosition.HasValue ? ns.RondelPosition.Value / (float)(RondelData.SlotCount - 1) : -1.0f;

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

            int targetSlot = (actingNs.RondelPosition.GetValueOrDefault() + act + 1) % RondelData.SlotCount;
            bool isPenalized = false;

            if (targetSlot == RondelData.FactorySlot)
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
            else if (targetSlot == RondelData.ImportSlot)
            {
                if (actingNs.Treasury < 1) isPenalized = true;
            }
            else if (RondelData.IsManeuverSlot(targetSlot))
            {
                bool hasUnits = game.Units.Any(u => u.Nation == actingNs.Nation);
                if (!hasUnits) isPenalized = true;
            }
            else if (RondelData.IsProductionSlot(targetSlot))
            {
                // Wasted Production: no unblockaded home factory can currently produce (every eligible
                // factory's unit type is already at cap). This branch was missing entirely - every other
                // actionable slot in this loop previews its own "wasted move" reward penalty, but
                // Production fell through to isPenalized=false unconditionally, so the agent had zero
                // signal before a choice the reward function (RondelData.IsProductionSlot branch further
                // down this file) penalizes -10 for. Same class of bug as the Investor gap above, found by
                // the same systematic check: does every reward penalty have a matching preview flag here.
                var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == actingNs.Nation);
                int currentArmies = game.Units.Count(u => u.Nation == actingNs.Nation && u.UnitType == UnitType.Army);
                int currentFleets = game.Units.Count(u => u.Nation == actingNs.Nation && u.UnitType == UnitType.Fleet);
                int maxArmies = NationData.GetMaxArmies(actingNs.Nation);
                int maxFleets = NationData.GetMaxFleets(actingNs.Nation);

                bool canProduceAnything = homeCities.Any(city =>
                {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                    if (ts == null || !ts.HasFactory) return false;
                    bool isBlockaded = game.Units.Any(u => u.TerritoryId == city.Id && u.Nation != actingNs.Nation && u.UnitType == UnitType.Army && u.IsHostile);
                    if (isBlockaded) return false;
                    return city.CityType == CityType.LightBlue ? currentFleets < maxFleets : currentArmies < maxArmies;
                });

                if (!canProduceAnything) isPenalized = true;
            }
            else if (targetSlot == RondelData.InvestorSlot)
            {
                // Landing on Investor pays interest on the acting nation's own bonds from its own treasury
                // (others first, then the controller); it's "penalized" whenever the controller nets <= 0
                // out of it — either paying a shortfall from their own pocket, or receiving nothing at all.
                // Reuses InvestorHelper.PreviewInterestPayment, the same non-mutating preview already used
                // for the PersonalContribution/MissedInterest reward penalties and the raw preview floats
                // appended later in this vector — this is not new game logic, just an added consumer of it.
                var moveController = game.Players.FirstOrDefault(p => p.Id == actingNs.ControllerId);
                if (moveController != null)
                {
                    var investorMovePreview = Helpers.InvestorHelper.PreviewInterestPayment(game, actingNs, moveController);
                    if (investorMovePreview.NetControllerCashDelta <= 0) isPenalized = true;
                }
            }
            else if (targetSlot == RondelData.TaxationSlot)
            {
                // Wasted Taxation: no power-track gain AND no treasury gain. Mirrors the reward penalty
                // for a fully wasted Taxation turn exactly ("expectedTaxTreasuryGain <= 0 &&
                // expectedTaxPowerGain == 0") by reading the SAME already-computed ExpectedTreasuryGain
                // field PreviewTaxation returns, rather than re-deriving an approximation of it from
                // TotalTaxRevenue - SoldiersPay. That approximation was wrong in one real case: when
                // treasury is low enough that actual soldiers' pay gets capped below the raw SoldiersPay
                // figure (TaxationHelper caps it at what's actually payable), the approximation could
                // read "wasted" while the real ExpectedTreasuryGain (which uses the capped payment) was
                // still positive - a false-positive flag telling the agent to avoid a Taxation visit that
                // the reward function would not actually have penalized.
                var taxMovePreview = Helpers.TaxationHelper.PreviewTaxation(game, actingNs);
                if (taxMovePreview.ExpectedPowerGain == 0 && taxMovePreview.ExpectedTreasuryGain <= 0) isPenalized = true;
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

        // === INVESTOR RISK CONTEXT (8 floats) ===
        // Appended at the end, not spliced into the per-nation block above, on purpose: this keeps every float
        // before this point at the exact same index it had before this feature existed, so an older model (one
        // whose input layer expects fewer floats) can be fed a plain prefix of this vector — via GetActionFromOnnx's
        // truncation — and still receive a byte-for-byte reproduction of the exact input it was trained on. Any
        // future state additions should follow the same append-only rule to preserve that.
        //
        // 1. Total interest owed per nation (6 floats). Range is fixed by the bond table (interests 1..9 sum to
        //    45), so this normalizes cleanly to [0,1] without clamping. Unlike Treasury, it isn't something the
        //    agent's own action can directly change, so it's given raw rather than as a pre-subtracted "deficit".
        foreach (var nation in imperial2030Nations)
        {
            state[i++] = bonds.Where(b => b.Nation == nation && b.HolderId != null).Sum(b => b.Interest) / 45.0f;
        }

        // 2. Investor outcome preview for the acting nation's controller (2 floats), mirroring the taxation
        // preview elsewhere in this vector. NetControllerCashDelta ranges [-45, 45] for the same reason as
        // above. The boolean is included separately because it's a much easier signal to key off of than
        // reading the exact float — the network doesn't need to calibrate "is this delta close enough to what's
        // owed" itself.
        var actingController = actingNs?.ControllerId != null ? game.Players.FirstOrDefault(p => p.Id == actingNs.ControllerId) : null;
        if (actingNs != null && actingController != null)
        {
            var investorPreview = Helpers.InvestorHelper.PreviewInterestPayment(game, actingNs, actingController);
            state[i++] = investorPreview.NetControllerCashDelta / 45.0f;
            state[i++] = investorPreview.WillGetFullOwnInterest ? 1.0f : 0.0f;
        }
        else
        {
            state[i++] = 0f;
            state[i++] = 0f;
        }

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
        var mask = new bool[RLBotStrategy.TotalActionSize];
        var rlPlayerId = session.RLPlayerId;
        if (game == null) return mask;

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return mask;

        // p.11: an Investor turn opened by the rondel move resolves before the destination's action, so
        // the rondel-action blocks below yield to it and the investor block answers instead.
        if (session.PendingFactoryDestructionTerritoryId != null && !game.IsInvestorTurn)
        {
            mask[RLBotStrategy.FactoryDestroyAction] = true;
            mask[RLBotStrategy.FactoryKeepAction] = true;
            return mask;
        }

        var factoryBuildNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        if (IsFactoryDecisionPending(session, factoryBuildNs, rlPlayerId) && factoryBuildNs != null)
        {
            mask[RLBotStrategy.FactorySkipAction] = true;

            if (factoryBuildNs.Treasury >= 5)
            {
                var (orderedHome, canBuild) = GetFactoryBuildOptions(game, factoryBuildNs);
                for (int slotIdx = 0; slotIdx < orderedHome.Count && slotIdx < RLBotStrategy.FactoryBuildActionCount; slotIdx++)
                {
                    if (canBuild[slotIdx]) mask[RLBotStrategy.FactoryBuildActionBase + slotIdx] = true;
                }
            }
            return mask;
        }

        if (IsImportSequencePending(session, game))
        {
            mask[RLBotStrategy.ImportStopAction] = true;

            if (session.PendingImportRemaining > 0)
            {
                var importNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
                if (importNs != null)
                {
                    var (orderedHome, canArmy, canFleet) = GetImportOptions(game, importNs);
                    for (int slotIdx = 0; slotIdx < orderedHome.Count && slotIdx < 4; slotIdx++)
                    {
                        if (canArmy[slotIdx]) mask[RLBotStrategy.ImportPlaceActionBase + slotIdx * 2] = true;
                        if (canFleet[slotIdx]) mask[RLBotStrategy.ImportPlaceActionBase + slotIdx * 2 + 1] = true;
                    }
                }
            }
            return mask;
        }

        if (game.PendingBattleDefenders.Any())
        {
            bool rlIsDefender = game.PendingBattleDefenders.Any(def =>
                game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));

            if (rlIsDefender)
            {
                mask[RLBotStrategy.FightAction] = true;
                mask[RLBotStrategy.RetreatAction] = true;
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
                mask[63] = true; // Pass (End Maneuver) — always available now that Stage 1 auto-selects

                // Find valid destinations
                var unitType = game.CurrentManeuverPhase == ManeuverPhase.Fleets ? UnitType.Fleet : UnitType.Army;
                var selectedUnit = units.FirstOrDefault(u => u.TerritoryId == session.ManeuverSelectedTerritoryId && u.UnitType == unitType && !u.HasMoved);

                if (selectedUnit != null)
                {
                    // Copy heuristic adjacency logic (simplified)
                    if (MapConnectivity.Adjacency.TryGetValue(selectedUnit.TerritoryId, out var neighbors))
                    {
                        var validNeighbors = neighbors.ToList();

                        if (unitType == UnitType.Fleet)
                        {
                            validNeighbors = validNeighbors.Where(n =>
                            {
                                if (!TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)) return false;

                                var canal = MapConnectivity.CanalLinks.FirstOrDefault(c =>
                                    (c.Region1 == selectedUnit.TerritoryId && c.Region2 == n) ||
                                    (c.Region1 == n && c.Region2 == selectedUnit.TerritoryId));

                                if (canal != default)
                                {
                                    var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == canal.ControllerId);
                                    if (tState != null && tState.Controller != null && tState.Controller != selectedUnit.Nation)
                                    {
                                        var canalNationState = game.NationStates.FirstOrDefault(ns => ns.Nation == tState.Controller.Value);
                                        if (canalNationState == null || canalNationState.ControllerId != session.RLPlayerId)
                                        {
                                            return false; // Canal blocked
                                        }
                                    }
                                }
                                return true;
                            }).ToList();
                            foreach (var dest in validNeighbors)
                            {
                                int idx = Array.IndexOf(RLBotStrategy.AllManeuverTerritories, dest);
                                if (idx >= 0) mask[127 + idx] = true;
                            }
                        }
                        else
                        {
                            var destinations = Imperial2030.Server.Helpers.ManeuverHelper.GetAllReachableArmyDestinations(game, selectedUnit.TerritoryId, selectedUnit.Nation);
                            foreach (var dest in destinations)
                            {
                                int idx = Array.IndexOf(RLBotStrategy.AllManeuverTerritories, dest.TerritoryId);
                                if (idx >= 0) mask[127 + idx] = true;
                            }
                        }
                    }
                }
            }
            return mask;
        }

        int currentPos = ns.RondelPosition ?? 0;

        for (int dist = 1; dist <= RondelData.MaxMoveDistance; dist++)
        {
            int targetSlot = (currentPos + dist) % RondelData.SlotCount;
            mask[dist - 1] = IsSlotValid(ns, rlPlayer, targetSlot);
        }

        mask[RondelData.MaxMoveDistance] = false; // Action 6 is unused for Rondel (moving 7 spaces is illegal in Imperial 2030)

        // Failsafe: if no actions are somehow valid, just force action 0 (move 1 space)
        if (!mask.Take(RondelData.MaxMoveDistance).Any(m => m)) mask[0] = true;

        return mask;
    }

    private bool IsSlotValid(NationState ns, Player rlPlayer, int targetSlot)
    {
        if (ns.RondelPosition.HasValue && ns.RondelPosition.Value == targetSlot) return false;

        int moveCost = RondelData.GetMoveCost(ns.RondelPosition, targetSlot, ns.Power);
        return rlPlayer.Cash >= moveCost;
    }

    // Home territories (ordered by Id, matching the encoding used elsewhere) for `ns.Nation`, with per-slot
    // legality of importing an Army or a Fleet there right now.
    private (List<Territory> OrderedHome, bool[] CanArmy, bool[] CanFleet) GetImportOptions(Game game, NationState ns)
    {
        // The ORDER is the action-space layout (ImportPlaceActionBase + index * 2) and must not change -
        // rule #17. Legality per slot is the engine's.
        var orderedHome = TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation).OrderBy(t => t.Id).ToList();

        var canArmy = new bool[orderedHome.Count];
        var canFleet = new bool[orderedHome.Count];
        for (int i = 0; i < orderedHome.Count; i++)
        {
            // No London exclusion here: that's a heuristic-only guard in BotStrategyBase to keep the
            // simple AI from stranding an army in a coastal city — the real game rules allow it, and the
            // RL policy should be free to judge that trade-off itself (it may even be the only open slot).
            canArmy[i] = ImportEngine.CanPlace(game, ns.Nation, orderedHome[i].Id, UnitType.Army);
            canFleet[i] = ImportEngine.CanPlace(game, ns.Nation, orderedHome[i].Id, UnitType.Fleet);
        }
        return (orderedHome, canArmy, canFleet);
    }

    // Objective event rewards stay separate from the holistic positional term. They are named here so
    // their established magnitudes are explicit and regression-tested while the Maneuver shaping changes.
    public const float FlagPlacementReward = 1.0f;
    public const float EnemyUnitDestroyedReward = 1.0f;
    public const float HomeReliefReward = 15.0f;
    public const float EnemyFactoryDestroyedReward = 3.0f;
    public const float EnemyFactoryOccupiedReward = 2.0f;

    // Holistic Maneuver weights. The whole result is capped below one home-relief event, so positional
    // shaping cannot dominate the direct military/economic outcome or the terminal game objective.
    public const float MaxManeuverOutcomeReward = 8.0f;
    public const float WastedManeuverResultPenalty = 2.0f;
    private const float OccupiedHomePenalty = 3.0f;
    private const float CoveredHomeThreatReward = 1.5f;
    private const float HomeDefenseDeficitPenalty = 2.0f;
    private const float FactoryDefenseDeficitPenalty = 2.0f;
    private const float ForwardStagingArmyReward = 1.0f;
    private const float UsefulReachObjectiveReward = 0.25f;
    private const int MaxUsefulReachObjectives = 12;
    private const float ManeuverPotentialEqualityTolerance = 0.0001f;

    public static bool TryArmManeuverOutcome(
        TrainingSession session,
        Game game,
        Nation nation,
        int targetSlot)
    {
        if (!RondelData.IsManeuverSlot(targetSlot) || session.ManeuverOutcome != null) return false;

        var nationState = game.NationStates.FirstOrDefault(state => state.Nation == nation);
        if (nationState?.ControllerId != session.RLPlayerId) return false;

        session.ManeuverOutcome = new ManeuverOutcomeSnapshot
        {
            Nation = nation,
            TurnCount = game.TurnCount,
            InitialStrategicPotential = CalculateManeuverStrategicPotential(
                game,
                nation,
                nationState.ControllerId)
        };
        return true;
    }

    /// <summary>
    /// Whether the step-by-step Import sequence is waiting for the agent's next placement: one is set up
    /// for the nation whose turn it is, and no Investor turn is open (p.11: an Investor turn opened by the rondel
    /// move resolves before the destination's action; the sequence resumes once it clears).
    /// </summary>
    public static bool IsImportSequencePending(TrainingSession session, Game game)
        => session.PendingImportRemaining.HasValue
           && session.ImportSequenceNation == game.CurrentTurnNation
           && !game.IsInvestorTurn;

    /// <summary>
    /// Whether a resolved rondel action may end the turn now: not while an Investor turn is open. Once it
    /// clears, BotService.ExecuteBotTurn finishes the turn on the RL agent's next step.
    /// </summary>
    public static bool MayRondelActionEndTheTurn(Game game)
        => game.Status == GameStatus.InProgress && !game.IsInvestorTurn;

    public static bool IsManeuverOutcomeReady(TrainingSession session, Game game)
    {
        var snapshot = session.ManeuverOutcome;
        return snapshot != null
            && game.Status == GameStatus.InProgress
            && snapshot.Nation == game.CurrentTurnNation
            && snapshot.TurnCount == game.TurnCount
            && game.CurrentManeuverPhase == ManeuverPhase.None
            && !game.IsInvestorTurn
            && !game.PendingBattleDefenders.Any()
            && game.PendingSwissBankForceNation == null
            && session.PendingFactoryDestructionTerritoryId == null;
    }

    public static float CompleteManeuverOutcome(TrainingSession session, float finalStrategicPotential)
    {
        var snapshot = session.ManeuverOutcome;
        if (snapshot == null) return 0f;

        float reward = CalculateManeuverOutcomeReward(snapshot, finalStrategicPotential);
        session.ManeuverOutcome = null;
        return reward;
    }

    public static float CalculateManeuverOutcomeReward(
        ManeuverOutcomeSnapshot snapshot,
        float finalStrategicPotential)
    {
        float delta = finalStrategicPotential - snapshot.InitialStrategicPotential;
        if (snapshot.AnyUnitChangedTerritory
            && !snapshot.DirectEventOccurred
            && delta <= ManeuverPotentialEqualityTolerance)
        {
            // This is one whole-phase verdict, not another per-move penalty: a rearrangement that did
            // not improve anything costs at least the small wasted-result amount, while a genuinely
            // damaging result keeps its larger (bounded) positional loss.
            delta = Math.Min(delta, -WastedManeuverResultPenalty);
        }

        if (MathF.Abs(delta) <= ManeuverPotentialEqualityTolerance) return 0f;
        return Math.Clamp(delta, -MaxManeuverOutcomeReward, MaxManeuverOutcomeReward);
    }

    /// <summary>
    /// Positional value used only at the two boundaries of one completed Maneuver. It deliberately omits
    /// flag, kill, factory-destruction, factory-occupation, and home-relief event counts: those retain
    /// their immediate rewards. Instead it measures whether the resulting board is safer and whether its
    /// forces are usefully placed for the next Maneuver.
    /// </summary>
    public static float CalculateManeuverStrategicPotential(Game game, Nation nation, Guid? controllerId)
    {
        var friendlyNations = game.NationStates
            .Where(state => controllerId.HasValue && state.ControllerId == controllerId)
            .Select(state => state.Nation)
            .Append(nation)
            .ToHashSet();

        float potential = 0f;
        foreach (var defense in HomeDefenseHelper.Assess(game, nation, controllerId))
        {
            int coveredThreat = Math.Min(defense.FriendlyArmyDefenders, defense.LandThreat);
            potential += coveredThreat * CoveredHomeThreatReward;
            potential -= defense.HostileOccupiers * OccupiedHomePenalty;
            potential -= defense.LandDefenseDeficit * HomeDefenseDeficitPenalty;

            bool hasFactory = game.TerritoryStates.Any(state =>
                state.TerritoryId == defense.Territory.Id && state.HasFactory);
            if (hasFactory)
            {
                potential -= defense.LandDefenseDeficit * FactoryDefenseDeficitPenalty;
            }
        }

        // Convoy carriers are marked used until AdvanceTurn. The potential describes the next Maneuver,
        // when that transient flag is reset, so temporarily normalize it and restore it in a finally.
        var convoyedFleets = game.Units.Where(unit => unit.HasConvoyed).ToList();
        foreach (var fleet in convoyedFleets) fleet.HasConvoyed = false;
        try
        {
            var usefulObjectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in game.Units.Where(unit => unit.Nation == nation))
            {
                foreach (string destinationId in ReachableDestinationsForPotential(game, unit))
                {
                    if (IsUsefulManeuverObjective(game, destinationId, nation, friendlyNations))
                    {
                        usefulObjectives.Add(destinationId);
                    }
                }
            }

            potential += Math.Min(usefulObjectives.Count, MaxUsefulReachObjectives)
                * UsefulReachObjectiveReward;

            var neutralStagingGroups = game.Units
                .Where(unit => unit.Nation == nation && unit.UnitType == UnitType.Army)
                .GroupBy(unit => unit.TerritoryId)
                .Where(group => IsControlledNeutralStagingRegion(game, group.Key, nation));

            foreach (var group in neutralStagingGroups)
            {
                bool reachesObjective = ManeuverHelper.GetAllReachableArmyDestinations(game, group.Key, nation)
                    .Any(destination => IsUsefulManeuverObjective(
                        game,
                        destination.TerritoryId,
                        nation,
                        friendlyNations));
                if (!reachesObjective) continue;

                potential += Math.Min(group.Count(), ManeuverRules.DestroyFactoryArmyCost)
                    * ForwardStagingArmyReward;
            }
        }
        finally
        {
            foreach (var fleet in convoyedFleets) fleet.HasConvoyed = true;
        }

        return potential;
    }

    private static IEnumerable<string> ReachableDestinationsForPotential(Game game, Unit unit)
    {
        if (unit.UnitType == UnitType.Army)
        {
            return ManeuverHelper.GetAllReachableArmyDestinations(game, unit.TerritoryId, unit.Nation)
                .Select(destination => destination.TerritoryId);
        }

        return MapConnectivity.GetNeighbors(unit.TerritoryId, isFleet: true);
    }

    private static bool IsControlledNeutralStagingRegion(Game game, string territoryId, Nation nation)
    {
        var territory = TerritoryData.AllTerritories.FirstOrDefault(candidate => candidate.Id == territoryId);
        if (territory == null || territory.Type != TerritoryType.Land || territory.Nation.HasValue) return false;

        var occupyingUnits = game.Units.Where(unit => unit.TerritoryId == territoryId).ToList();
        if (occupyingUnits.Any(unit => unit.Nation != nation)) return false;

        var state = game.TerritoryStates.FirstOrDefault(candidate => candidate.TerritoryId == territoryId);
        if (state?.Controller == nation) return true;

        return occupyingUnits.Count > 0 && occupyingUnits.All(unit => unit.Nation == nation);
    }

    private static bool IsUsefulManeuverObjective(
        Game game,
        string territoryId,
        Nation nation,
        ISet<Nation> friendlyNations)
    {
        var territory = TerritoryData.AllTerritories.FirstOrDefault(candidate => candidate.Id == territoryId);
        if (territory == null) return false;

        if (territory.Nation == nation)
        {
            return game.Units.Any(unit =>
                unit.TerritoryId == territoryId
                && !friendlyNations.Contains(unit.Nation)
                && unit.IsHostile);
        }

        if (territory.Nation.HasValue)
        {
            return !friendlyNations.Contains(territory.Nation.Value);
        }

        var occupyingUnits = game.Units.Where(unit => unit.TerritoryId == territoryId).ToList();
        if (occupyingUnits.Any(unit => !friendlyNations.Contains(unit.Nation))) return true;

        var state = game.TerritoryStates.FirstOrDefault(candidate => candidate.TerritoryId == territoryId);
        if (state?.Controller is Nation controller && friendlyNations.Contains(controller)) return false;

        return occupyingUnits.Count == 0;
    }

    /// <summary>
    /// Hard cap on how many agent steps one training episode may take. Measured ep_len_mean is ~61, so
    /// this is roughly 30x a normal game - only a genuinely pathological session reaches it.
    /// </summary>
    public const int MaxSessionSteps = 2000;

    /// <summary>
    /// Hard cap on steps spent without the turn counter advancing. Catches a decision loop that re-derives
    /// the same candidate forever, which is a different failure from a game that is merely long.
    /// </summary>
    public const int MaxConsecutiveSameTurnSteps = 50;

    /// <summary>
    /// Writes a halted session's game to JSON beside the other training artifacts, for replay.
    ///
    /// Best-effort by design: this runs on a path that is already failing, and an export problem must not
    /// replace the halt reason with a serialization stack trace. A failure here is logged and swallowed -
    /// the throw that follows is what matters.
    /// </summary>
    private void TryExportStuckGame(Game game, string sessionId)
    {
        try
        {
            var export = new Imperial2030.Shared.Models.GameExportDto
            {
                FormatVersion = 1,
                OriginalGameId = game.Id,
                OriginalGameName = game.Name,
                ExportedAt = DateTime.UtcNow,
                Actions = game.Actions
                    .OrderBy(a => a.OrderIndex).ThenBy(a => a.Timestamp)
                    .Select(a => new Imperial2030.Shared.Models.GameActionDto
                    {
                        Id = a.Id,
                        OrderIndex = a.OrderIndex,
                        Timestamp = a.Timestamp,
                        PlayerName = a.PlayerName,
                        Nation = a.Nation,
                        ActionType = a.ActionType,
                        Message = a.Message,
                        Metadata = a.Metadata ?? string.Empty
                    }).ToList()
            };

            var path = Path.Combine(AppContext.BaseDirectory, $"stuck_session_{sessionId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));

            // The action log alone cannot answer basic questions about the halted position - it carries no
            // player cash, no bond holders, no unit positions, no per-nation power or treasury. Analysing
            // the first halted export meant inferring all of that from action metadata, which produced
            // several confidently wrong readings before the log was found to simply not contain the
            // answers. Everything needed is dumped here instead, in a SEPARATE file so
            // stuck_session_<id>.json stays a valid GameExportDto that imports and replays unchanged.
            var state = new
            {
                SessionId = sessionId,
                CapturedAt = DateTime.UtcNow,
                Game = new
                {
                    game.Id,
                    game.Name,
                    Status = game.Status.ToString(),
                    game.TurnCount,
                    CurrentTurnNation = game.CurrentTurnNation.ToString(),
                    CurrentManeuverPhase = game.CurrentManeuverPhase.ToString(),
                    game.IsInvestorTurn,
                    game.ActingPlayerId,
                    game.InvestorCardHolderId,
                    game.PendingBattleTerritoryId,
                    PendingBattleAggressorNation = game.PendingBattleAggressorNation?.ToString(),
                    game.PendingBattleAggressorUnitId,
                    PendingBattleDefenders = game.PendingBattleDefenders.Select(n => n.ToString()).ToList(),
                    PendingSwissBankForceNation = game.PendingSwissBankForceNation?.ToString(),
                    game.VariantBonusOnlyForTaxIncreases
                },
                Players = game.Players.Select(pl => new
                {
                    pl.Id,
                    pl.BotName,
                    pl.BotType,
                    pl.IsBot,
                    pl.IsHost,
                    pl.Cash,
                    BondCount = game.Bonds.Count(b => b.HolderId == pl.Id),
                    BondCredit = game.Bonds.Where(b => b.HolderId == pl.Id).Sum(b => b.Cost),
                    Score = game.CalculateScore(pl.Id)
                }).ToList(),
                NationStates = game.NationStates.Select(ns => new
                {
                    Nation = ns.Nation.ToString(),
                    ns.ControllerId,
                    ns.Power,
                    ns.Treasury,
                    ns.RondelPosition,
                    ns.HasMovedThisTurn,
                    ns.HasBuiltThisTurn,
                    ns.HasProducedThisTurn,
                    ns.HasImportedThisTurn,
                    ns.TaxRevenue,
                    ns.PreviousTaxRevenue
                }).ToList(),
                Bonds = game.Bonds.Select(b => new
                {
                    Nation = b.Nation.ToString(),
                    b.Cost,
                    b.Interest,
                    b.HolderId
                }).ToList(),
                Units = game.Units.Select(u => new
                {
                    Nation = u.Nation.ToString(),
                    UnitType = u.UnitType.ToString(),
                    u.TerritoryId,
                    u.IsHostile,
                    u.HasMoved,
                    u.HasConvoyed
                }).ToList(),
                TerritoryStates = game.TerritoryStates
                    .Where(t => t.HasFactory || t.Controller != null)
                    .Select(t => new { t.TerritoryId, t.HasFactory, Controller = t.Controller?.ToString() })
                    .ToList()
            };

            var statePath = Path.Combine(AppContext.BaseDirectory, $"stuck_session_{sessionId}_state.json");
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

            _logger.LogWarning(
                $"[RL DIAGNOSTIC] Exported the halted game to '{path}' ({export.Actions.Count} actions) - " +
                $"import it to replay - and the full board state to '{statePath}'.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RL DIAGNOSTIC] Could not export halted session {SessionId}; continuing to the halt.", sessionId);
        }
    }

    /// <summary>
    /// Why this session should be abandoned, or null to continue. Pure so the limits can be tested without
    /// standing up a session and a socket.
    /// </summary>
    public static string? SessionHaltReason(int totalSteps, int consecutiveSameTurnSteps)
    {
        if (totalSteps > MaxSessionSteps)
        {
            return $"exceeded {MaxSessionSteps} steps without finishing";
        }

        if (consecutiveSameTurnSteps > MaxConsecutiveSameTurnSteps)
        {
            return $"stalled for {consecutiveSameTurnSteps} consecutive steps without advancing the turn";
        }

        return null;
    }

    /// <summary>
    /// Whether the RL agent still owes a Factory build/skip decision for the nation whose turn it is.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="TrainingSession.FactoryDecisionOwedBy"/>, which the rondel move sets on landing
    /// here - not on <c>RondelPosition == FactorySlot</c>, which persists across turns and would say the
    /// decision is owed again on every later turn (Tests/TrainingFactorySlotTrapTests.cs).
    /// </remarks>
    public static bool IsFactoryDecisionPending(TrainingSession session, NationState? currentNs, Guid rlPlayerId)
        => currentNs != null && currentNs.ControllerId == rlPlayerId
           && session.FactoryDecisionOwedBy == currentNs.Nation && !currentNs.HasBuiltThisTurn
           // p.11: an Investor turn opened by the rondel move resolves before the destination's action.
           // The decision stays owed and is asked once the phase clears.
           && !session.Game.IsInvestorTurn;

    public const float AvoidableFactorySkipPenalty = 30.0f;

    /// <summary>
    /// Paid for a successful factory build.
    ///
    /// Raised from 10 because the expected value of even *visiting* the Factory slot was negative. A
    /// nation normally gets just two builds per game - four home cities, one factory each
    /// (Imperial-2030-Rules.pdf p.7), two of them already built at setup (p.4) - plus any it rebuilds
    /// after an enemy destroys one (p.11), which is uncommon. So across a ~20-move nation-stint the agent
    /// gets roughly two payoffs and a long tail of dead landings. Landing on
    /// Factory unable to build costs <see cref="WastedFactoryActionPenalty"/>, plus
    /// <see cref="AllFactoriesBuiltPenalty"/> once both are up - so at +10 a landing needed better than a
    /// 57% chance of being buildable just to break even, and after the second build that chance is zero.
    /// At 16 the break-even is ~45%, which makes attempting under uncertainty rational rather than a
    /// mistake the agent has to be lucky to survive.
    /// </summary>
    public const float FactoryBuildReward = 16.0f;

    /// <summary>Landing on Factory when no build was possible. Was an inline -8.</summary>
    public const float WastedFactoryActionPenalty = 8.0f;

    /// <summary>Charged on top of <see cref="WastedFactoryActionPenalty"/> when every home city already
    /// has its factory - the one wasted landing the agent could always have foreseen.</summary>
    public const float AllFactoriesBuiltPenalty = 5.0f;

    /// <summary>
    /// Charged instead when the nation can afford the factory but not the factory AND a full Import.
    ///
    /// Below <see cref="FactoryAndFullImportCost"/> the two purchases genuinely compete for the same
    /// treasury, so holding the money can be a real plan rather than a wasted turn — the agent may be on
    /// its way to Import. Still non-zero, and deliberately so: a factory produces a unit every Production
    /// turn and adds 2M of tax revenue for the rest of the game, whereas an import buys units once, so
    /// the trade is usually still wrong. It is an excuse, not a justification.
    /// </summary>
    public const float ReducedFactorySkipPenalty = 10.0f;

    /// <summary>
    /// What a nation needs to do both: 5M for the factory plus a full Import of
    /// RLBotStrategy.MaxImportUnits units at 1M each (GamesController.ExecuteImport: `cost = Units.Count`).
    /// Derived rather than written as 8 so it cannot drift from either rule.
    /// </summary>
    private const int FactoryAndFullImportCost = FactoryCost + RLBotStrategy.MaxImportUnits;

    /// <summary>
    /// The penalty owed for however this factory decision was resolved: 0 when the skip was not
    /// avoidable at all, the reduced rate when building would have starved an Import, otherwise the
    /// full rate.
    /// </summary>
    public static float AvoidableFactorySkipPenaltyFor(Game game, NationState ns)
    {
        if (!WasAvoidableFactorySkip(game, ns)) return 0f;

        return ns.Treasury < FactoryAndFullImportCost
            ? ReducedFactorySkipPenalty
            : AvoidableFactorySkipPenalty;
    }

    /// <summary>
    /// True when the acting nation declined a factory it could actually have built: treasury covers the
    /// 5M cost and at least one home city is free and unblockaded.
    ///
    /// Scoped narrowly on purpose. "No treasury" and "every city built or occupied" are already covered,
    /// more precisely, by the wasted-Factory-action penalty applied to the rondel move itself, and firing
    /// for those here would double-penalize one event (.agents/AGENTS.md rule #25). Reuses
    /// GetFactoryBuildOptions rather than re-deriving buildability so the two cannot drift.
    /// </summary>
    public static bool WasAvoidableFactorySkip(Game game, NationState ns)
    {
        if (ns.Treasury < FactoryCost) return false;

        var (_, canBuild) = GetFactoryBuildOptionsFor(game, ns);
        return canBuild.Any(c => c);
    }

    /// <summary>The rulebook's factory price (Imperial-2030-Rules.pdf p.7: "The nation pays 5 million").</summary>
    private const int FactoryCost = GameConstants.FactoryCost;

    /// <summary>
    /// True when <paramref name="unit"/> leaving <paramref name="vacatedTerritoryId"/> for
    /// <paramref name="target"/> strips the last army defender from one of its own factory cities while
    /// an enemy army is in range — and the move is not itself an answer to that threat.
    ///
    /// Reachability is full maneuver reachability (land adjacency, rail, convoy), not plain adjacency: a
    /// fleet-convoyed army several sea zones away is just as much of a threat. It is deliberately not
    /// filtered by IsHostile, which describes how a unit is sitting where it already is, not whether it
    /// could turn hostile next turn.
    ///
    /// Two moves are exempt, because both REMOVE the risk rather than ignoring it — penalizing them
    /// would teach the agent to sit still and let an enemy walk into the city:
    ///
    ///   * moving onto the very enemy army that could have reached the city (a preventative strike — the
    ///     threat being counted is the thing being attacked);
    ///   * moving onto one of the nation's own home provinces that an enemy is occupying (defending home
    ///     ground). Any enemy unit counts here, not just an army: an enemy fleet sitting in a home city's
    ///     harbor is still an enemy on home soil worth clearing.
    ///
    /// The two exemptions treat the hostility flag differently, because the engine does:
    ///
    ///   * Into the nation's OWN home province holding a foreign unit, a battle is guaranteed whatever
    ///     the caller asked for — ManeuverController overrides the flag ("Foreign armies in your home
    ///     territory are always hostile. You cannot peacefully coexist."). This exemption therefore does
    ///     not read isHostileMove at all. It matches Imperial-2030-Rules.pdf, where the hostile/friendly
    ///     choice (p.10) is about entering the home province of ANOTHER Great Power.
    ///   * Anywhere else, the flag decides. A hostile move resolves as combat, but a FRIENDLY move only
    ///     opens battle negotiation, which the inactive defender may decline (FAQ p.14: they are
    ///     *allowed* to fight, not obliged) — both units then simply coexist in the region and the threat
    ///     to the vacated city is completely untouched. So a friendly move onto the threat answers
    ///     nothing and earns no exemption.
    ///
    /// This split is what keeps the predicate correct once the hostility decision is handed to the model.
    /// RLBotStrategy currently hardcodes DetermineHostility to true whenever an enemy is present ("like
    /// it did before"), so today the flag is always true here; other strategies already vary, with
    /// BotStrategyBase choosing randomly. Reading the flag where it genuinely changes the outcome, and
    /// ignoring it where the engine overrides it, means neither a future model policy nor a different
    /// strategy can silently change what this reward means.
    /// </summary>
    public static bool IsRecklessFactoryCityVacation(Game game, Unit unit, string vacatedTerritoryId, string target, bool isHostileMove)
    {
        if (unit.UnitType != UnitType.Army) return false;
        if (target == vacatedTerritoryId) return false;

        var originDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == vacatedTerritoryId);
        if (originDef == null || originDef.Nation != unit.Nation) return false;

        var originTState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == vacatedTerritoryId);
        if (originTState == null || !originTState.HasFactory) return false;

        bool remainingDefenders = game.Units.Any(u => u.Id != unit.Id
            && u.TerritoryId == vacatedTerritoryId && u.Nation == unit.Nation && u.UnitType == UnitType.Army);
        if (remainingDefenders) return false;

        // Kept as the threatening units themselves, not just a bool: the preventative-strike exemption
        // below has to ask whether the destination holds one of them.
        var threats = game.Units
            .Where(u => u.Nation != unit.Nation && u.UnitType == UnitType.Army)
            .Where(enemy => Imperial2030.Server.Helpers.ManeuverHelper
                .GetAllReachableArmyDestinations(game, enemy.TerritoryId, enemy.Nation)
                .Any(d => d.TerritoryId == vacatedTerritoryId))
            .ToList();

        if (threats.Count == 0) return false;

        // Defending home ground: an enemy of any kind sitting in one of this nation's own home provinces.
        // No hostility check — the engine forces the battle here. Any foreign unit counts, not just an
        // army: an enemy fleet in a home city's harbor is still an enemy on home soil worth clearing.
        var targetDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
        bool targetIsOwnHome = targetDef != null && targetDef.Nation == unit.Nation;
        bool enemyAtTarget = game.Units.Any(u => u.TerritoryId == target && u.Nation != unit.Nation);
        if (targetIsOwnHome && enemyAtTarget) return false;

        // Preventative strike: the destination holds one of the very armies counted as a threat above.
        // Hostile only — a friendly arrival can end in the defender declining battle and both units
        // sitting there together, leaving the threat free to take the vacated city next turn.
        if (isHostileMove && threats.Any(t => t.TerritoryId == target)) return false;

        return true;
    }

    private (List<Territory> OrderedHome, bool[] CanBuild) GetFactoryBuildOptions(Game game, NationState ns)
        => GetFactoryBuildOptionsFor(game, ns);

    private static (List<Territory> OrderedHome, bool[] CanBuild) GetFactoryBuildOptionsFor(Game game, NationState ns)
    {
        // The ORDER is the action-space layout (FactoryBuildActionBase + index) and must not change - rule
        // #17: every home province by Id, as the deployed models were trained on. Legality per slot is the
        // engine's (which also requires a city; on this map every home province is one).
        var orderedHome = TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation).OrderBy(t => t.Id).ToList();
        var canBuild = new bool[orderedHome.Count];
        for (int i = 0; i < orderedHome.Count; i++)
        {
            canBuild[i] = FactoryEngine.IsBuildableSite(game, ns.Nation, orderedHome[i].Id);
        }
        return (orderedHome, canBuild);
    }
}
