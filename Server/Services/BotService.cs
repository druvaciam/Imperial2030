using Imperial2030.Server.Data;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Imperial2030.Server.Hubs;

namespace Imperial2030.Server.Services;

public class BotService
{
    private const int RondelMoveCostScorePenalty = 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly IEnumerable<Bots.IBotStrategy> _botStrategies;
    private readonly ILogger<BotService> _logger;
    public const int BotDelayMs = 5000;

    private const int FactoryCost = GameConstants.FactoryCost;
    private const int ImportUnitCost = GameConstants.ImportUnitCost;
    public bool SkipDelays { get; set; } = false;

    public BotService(IServiceScopeFactory scopeFactory, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, IEnumerable<Bots.IBotStrategy> botStrategies, ILogger<BotService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _botStrategies = botStrategies;
        _logger = logger;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bots.IBotStrategy> _rlStrategies = new();

    public Bots.IBotStrategy GetStrategy(Player player)
    {
        var type = player.BotType ?? "Default";

        // Handle RL bots dynamically
        if (type.StartsWith("RL", StringComparison.OrdinalIgnoreCase))
        {
            // Keyed by (type, player.Id), not type alone: RLBotStrategy carries non-thread-safe per-decision
            // caching state (_lastState/_cachedAction/_maneuverCache). Keying by type alone made every
            // concurrent game with the same bot type (e.g. "RL-2") share one instance process-wide — harmless
            // with a single training env (only one game steps at a time) but a genuine data race with
            // multiple parallel envs, where two games' opponent-bot decisions could interleave on the same
            // mutable fields and corrupt each other's cached action. The underlying ONNX InferenceSession
            // stays shared via _sessionCache (keyed by model path) regardless, so this costs nothing extra.
            var key = $"{type}:{player.Id}";
            return _rlStrategies.GetOrAdd(key, _ => new Bots.Strategies.RLBotStrategy(type, _logger));
        }

        return _botStrategies.FirstOrDefault(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? _botStrategies.FirstOrDefault(s => s.Name == "Default")
               ?? new Bots.Strategies.DefaultBotStrategy(); // Fallback if not registered
    }

    // Per-(type, player.Id) keying in GetStrategy fixes a cross-game data race (see comment above) but means
    // _rlStrategies grows one entry per bot per training episode, since each RL_Training_ game mints fresh
    // Player GUIDs on every reset. Call this whenever a training session ends (normally or via a dropped
    // connection) to release that game's entries instead of leaking them for the life of the server process.
    public void ClearStrategyCache(IEnumerable<Player> players)
    {
        foreach (var player in players)
        {
            var type = player.BotType ?? "Default";
            if (type.StartsWith("RL", StringComparison.OrdinalIgnoreCase))
            {
                _rlStrategies.TryRemove($"{type}:{player.Id}", out _);
            }
        }
    }

    public void TriggerBotTurn(Guid gameId, int delayMs = BotDelayMs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!SkipDelays && delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }
                await TryPlayBotTurnAsync(gameId);
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an exception escaping here becomes an
                // UnobservedTaskException: raised only when the task is garbage collected, long after the
                // fact, and swallowed by default. Observed here instead, with the game it belongs to —
                // otherwise a bot that dies mid-turn is indistinguishable from one that had nothing to do,
                // which is the same silent-stall symptom the wakeup latch above exists to prevent.
                _logger.LogError(ex, "Background bot turn failed for game {GameId}", gameId);
            }
        });
    }

    // Only one bot loop runs per game at a time; this is the claim on that slot.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _activeBotGames = new();

    // "A bot may have work in this game." Recorded by every caller, INCLUDING the ones that find a loop
    // already running and return - otherwise their request is simply lost.
    //
    // Bots have no clock: they act only when TriggerBotTurn says something changed. A request that arrives
    // while a loop is on its way out - it has decided it has nothing left to do but not yet released its
    // claim - must not be dropped, or nobody is running and nobody is coming, and the game sits forever
    // waiting on a bot (e.g. for the battle response a human's move just asked for).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _pendingBotWakeups = new();

    /// <summary>Test seam: whether a wakeup request is recorded but not yet consumed.</summary>
    internal static bool HasPendingWakeup(Guid gameId) => _pendingBotWakeups.ContainsKey(gameId);

    /// <summary>Test seam: claim/release the single-loop slot without running a real bot loop.</summary>
    internal static bool TryClaimBotLoopSlot(Guid gameId) => _activeBotGames.TryAdd(gameId, true);
    internal static void ReleaseBotLoopSlot(Guid gameId) => _activeBotGames.TryRemove(gameId, out _);

    public async Task TryPlayBotTurnAsync(Guid gameId, bool singleTurnOnly = false)
    {
        // Recorded BEFORE the slot is claimed, so a loop that is already running is guaranteed to see it.
        _pendingBotWakeups[gameId] = true;

        if (!_activeBotGames.TryAdd(gameId, true)) return;

        try
        {
            while (true)
            {
                // Consumed at the START of the pass that will service it, so a request arriving DURING
                // this pass leaves a fresh mark behind instead of being swallowed by this one.
                _pendingBotWakeups.TryRemove(gameId, out _);

                using var scope = _scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                Game? game = await LoadGame(ctx, gameId);
                if (game == null || game.Status != GameStatus.InProgress || game.IsPaused) break;

                bool botActed = false;

                // Handle bot Investor turn
                if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
                {
                    var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
                    if (actor != null && actor.IsBot)
                    {
                        try
                        {
                            if (!SkipDelays)
                            {
                                // Beat BEFORE the decision rather than after it. An Investor turn is opened
                                // by the rondel move that just landed on Investor, or by the end of the
                                // turn of a nation whose move passed over it, so acting
                                // straight away made that move and the resulting investment land in the same
                                // instant, followed by a dead pause with nothing to watch. Same placement and
                                // reasoning as the Swiss Bank branch below. One delay per action either way,
                                // so overall pacing is unchanged — only where the beat falls.
                                await Task.Delay(BotDelayMs);

                                // The wait means `game` may be stale (a human Swiss Bank investor could have
                                // acted meanwhile, ending the phase or moving it to a different player), so
                                // reload and re-resolve before deciding — again mirroring Swiss Bank.
                                game = await ReloadGameAsync(ctx, game);
                                if (game == null) break;
                                actor = game.IsInvestorTurn && game.ActingPlayerId.HasValue
                                    ? game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId.Value)
                                    : null;
                            }

                            if (actor != null && actor.IsBot)
                            {
                                await BotInvestorAction(ctx, game, actor);
                                await SaveChangesAsync(ctx);
                                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                                botActed = true;
                            }
                        }
                        catch (Bots.Strategies.RlTrainingPauseException)
                        {
                            break;
                        }
                    }
                }
                else if (game.PendingBattleDefenders.Any())
                {
                    try
                    {
                        await HandleBotBattleResponse(ctx, game);
                        botActed = true;
                    }
                    catch (Bots.Strategies.RlTrainingPauseException)
                    {
                        break; // Pause loop so RL Python env can fetch state
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
                            if (!SkipDelays) await Task.Delay(BotDelayMs); // add some delay before bot responds

                            game = await ReloadGameAsync(ctx, game);
                            if (game == null) break;

                            botResponders = game.PendingSwissBankResponders
                                .Select(id => game.Players.FirstOrDefault(p => p.Id == id))
                                .Where(p => p != null && p.IsBot)
                                .ToList()!;

                            await HandleBotSwissBankResponse(ctx, game, botResponders);
                            botActed = true;
                        }
                        catch (Bots.Strategies.RlTrainingPauseException)
                        {
                            break;
                        }
                    }
                    else
                    {
                        // Waiting for human Swiss Bank player
                    }
                }
                else
                {
                    var nationState = game.NationStates.FirstOrDefault(ns => ns.Nation == game.CurrentTurnNation);
                    if (nationState?.ControllerId != null)
                    {
                        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
                        if (controller != null && controller.IsBot)
                        {
                            try
                            {
                                await ExecuteBotTurn(ctx, game, nationState, controller);
                                botActed = true;
                            }
                            catch (Bots.Strategies.RlTrainingPauseException)
                            {
                                // Pause the game loop so the Training Controller can fetch the next action from Python
                                break;
                            }
                        }
                    }
                    else if (nationState != null && nationState.ControllerId == null)
                    {
                        // Uncontrolled nation: automatically advance to next nation
                        game.AdvanceTurn();
                        GameLogger.LogAutoSkipManeuverPhase(ctx, game, "Turn", nationState.Nation, GameConstants.SystemPlayerName);
                        await SaveChangesAsync(ctx);
                        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                        botActed = true;
                    }
                }

                if (singleTurnOnly) break;

                // Nothing to do AND nothing newly asked for: safe to leave. If a request came in while
                // this pass ran, take another one rather than exiting and stranding it.
                if (!botActed && !_pendingBotWakeups.ContainsKey(gameId)) break;
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore during application shutdown or test teardown
        }
        finally
        {
            _activeBotGames.TryRemove(gameId, out _);
        }

        // The slot is free now. A request that landed between the last check above and that release saw
        // the slot still taken and returned, so nothing is running to service it - start a fresh loop.
        // This cannot spin: each pass consumes the mark, so an unprompted re-entry finds nothing to do
        // and leaves without setting it again.
        if (!singleTurnOnly && _pendingBotWakeups.ContainsKey(gameId))
        {
            TriggerBotTurn(gameId, delayMs: 0);
        }
    }

    public async Task ExecuteBotTurn(ApplicationDbContext? ctx, Game game, NationState nationState, Player controller)
    {
        var nation = nationState.Nation;
        var gameId = game.Id;

        int targetSlot;

        if (!nationState.HasMovedThisTurn)
        {
            // Step 1: Choose rondel slot
            targetSlot = ChooseRondelSlot(game, nationState, controller);

            var move = RondelEngine.MoveNation(ctx, game, nation, targetSlot);
            if (!move.Ok)
            {
                // ChooseRondelSlot only offers legal, affordable slots; a rejection is a bug in the selection.
                throw new InvalidOperationException($"Bot {controller.BotName} chose an illegal rondel move for {nation} to slot {targetSlot}: {move.Error}");
            }

            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

            if (move.SwissBankForcedStop)
            {
                return; // PAUSE bot turn until the Swiss Bank responders answer
            }

            if (game.IsInvestorTurn)
            {
                // Pause Bot turn until Investor Phase resolves
                return;
            }

            if (!SkipDelays) await Task.Delay(BotDelayMs);

            // Reload game state since we might have waited
            game = await ReloadGameAsync(ctx, game);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);
            controller = game.Players.First(p => p.Id == nationState.ControllerId);
        }
        else
        {
            targetSlot = nationState.RondelPosition.Value;
        }

        // Step 2: Execute rondel action

        // Watched so the pause below can be skipped when the slot logged nothing worth reading.
        int actionCountBeforeSlot = game.Actions.Count;

        switch (targetSlot)
        {
            case RondelData.TaxationSlot: await BotTaxation(ctx, game, nationState, controller); break;
            case RondelData.FactorySlot: await BotBuildFactory(ctx, game, nationState, controller); break;
            case RondelData.ProductionSlot1:
            case RondelData.ProductionSlot2: await BotProduction(ctx, game, nationState); break;
            case RondelData.ManeuverSlot1:
            case RondelData.ManeuverSlot2: await BotManeuver(ctx, game, nationState, controller); break;
            case RondelData.ImportSlot: await BotImport(ctx, game, nationState); break;
            case RondelData.InvestorSlot: break; // Investor handled separately
        }

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        // If not taxation (which auto-advances), not in maneuver, and not mid-Import/Build decision, end turn
        bool importPending = targetSlot == RondelData.ImportSlot && !nationState.HasImportedThisTurn;
        bool buildPending = targetSlot == RondelData.FactorySlot && !nationState.HasBuiltThisTurn;
        bool slotActionWasLogged = game.Actions.Count > actionCountBeforeSlot;

        if (targetSlot != RondelData.TaxationSlot && game.Status == GameStatus.InProgress && game.CurrentManeuverPhase == ManeuverPhase.None && !importPending && !buildPending)
        {
            // Only wait if there is something on screen to have read. The delay exists to let a viewer
            // take in what the bot just did; with no log entry it is dead time, and it stacked with the
            // post-rondel delay above to make a single silent turn take twice as long as a busy one.
            if (!SkipDelays && slotActionWasLogged) await Task.Delay(BotDelayMs);
            game = await ReloadGameAsync(ctx, game);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);

            // Advance turn
            var endTurn = TurnEngine.EndTurn(ctx, game);
            if (!endTurn.Ok)
            {
                // The condition above established the turn is finishable; a rejection is a bug in that condition.
                throw new InvalidOperationException($"Bot {controller.BotName} could not end {nation}'s turn: {endTurn.Error}");
            }
            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        }

        // Check if next turn is also a bot
        if (!SkipDelays)
        {
            await Task.Delay(BotDelayMs);
        }
    }

    private int ChooseRondelSlot(Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;
        int factoryCount = CountFactories(game, nation);
        int unitCount = game.Units.Count(u => u.Nation == nation);
        var strategy = GetStrategy(controller);

        var candidates = new List<(int Slot, double Score)>();
        double maxScore = -999;
        int fallbackSlot = ns.RondelPosition.HasValue ? ((ns.RondelPosition.Value + 1) % RondelData.SlotCount) : RondelData.ProductionSlot1;
        int bestSlot = fallbackSlot;

        for (int slot = 0; slot < RondelData.SlotCount; slot++)
        {
            if (ns.RondelPosition.HasValue && slot == ns.RondelPosition.Value) continue;

            int moveCost = 0;
            if (ns.RondelPosition.HasValue)
            {
                int dist = RondelData.GetMoveDistance(ns.RondelPosition.Value, slot);
                if (dist > RondelData.MaxMoveDistance) continue;

                moveCost = RondelData.GetMoveCost(ns.RondelPosition, slot, ns.Power);
            }

            if (moveCost > controller.Cash) continue;

            // Prevent useless Investor pass-through if treasury is 0 (heuristic bots only —
            // the RL bot's own trained policy should be free to judge this trade-off itself)
            if (slot == RondelData.InvestorSlot && ns.Treasury == 0 && !(strategy is RLBotStrategy)) continue;

            double score = GetAdjustedRondelCandidateScore(
                strategy,
                slot,
                game,
                ns,
                controller,
                factoryCount,
                unitCount);

            if (score > maxScore)
            {
                maxScore = score;
                bestSlot = slot;
            }

            if (score > 0)
            {
                candidates.Add((slot, score));
            }
        }

        if (!candidates.Any()) return bestSlot;

        // Deduplicate identical actions (Production: 2/6, Maneuver: 3/7)
        // by keeping only the highest scoring one so the bot doesn't randomly pay more for the same action.
        int GetActionGroup(int s) => s switch { 2 or 6 => 2, 3 or 7 => 3, _ => s };
        candidates = candidates
            .GroupBy(c => GetActionGroup(c.Slot))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .ToList();

        double highestCandidateScore = candidates.Max(candidate => candidate.Score);
        var weightedCandidates = candidates
            .Select(candidate => (
                candidate.Slot,
                Weight: GetRondelSelectionWeight(strategy, candidate.Score, highestCandidateScore)))
            .ToList();

        double totalWeight = weightedCandidates.Sum(candidate => candidate.Weight);
        double roll = Random.Shared.NextDouble() * totalWeight;

        double current = 0;
        foreach (var candidate in weightedCandidates)
        {
            current += candidate.Weight;
            if (roll <= current) return candidate.Slot;
        }

        return weightedCandidates.Last().Slot;
    }

    /// <summary>
    /// Default remains random, but uses a softmax weight so a clearly superior move is overwhelmingly
    /// likely and near-equal moves retain meaningful variety. Other personalities preserve their linear
    /// score weighting.
    /// </summary>
    internal static double GetRondelSelectionWeight(
        IBotStrategy strategy,
        double candidateScore,
        double highestCandidateScore)
    {
        return strategy.GetRondelSelectionWeight(candidateScore, highestCandidateScore);
    }

    internal static double GetAdjustedRondelCandidateScore(
        IBotStrategy strategy,
        int slot,
        Game game,
        NationState nationState,
        Player controller,
        int factoryCount,
        int unitCount)
    {
        double score = strategy.ScoreRondelSlot(
            slot,
            game,
            nationState,
            controller,
            factoryCount,
            unitCount);
        int moveCost = RondelData.GetMoveCost(nationState.RondelPosition, slot, nationState.Power);

        bool ignoreCost = moveCost > 0
            && slot == RondelData.TaxationSlot
            && TaxationHelper.IsProjectedWinningGameEndingTaxation(game, nationState, controller);

        return ignoreCost ? score : score - moveCost * RondelMoveCostScorePenalty;
    }

    private int CountFactories(Game game, Nation nation)
    {
        return game.TerritoryStates.Count(ts => ts.HasFactory &&
            TerritoryData.AllTerritories.Any(t => t.Id == ts.TerritoryId && t.Nation == nation));
    }

    // --- Slot Action Implementations ---

    // internal for the same reason as BotManeuver: Tests drives this one rondel action directly.
    internal async Task BotBuildFactory(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        // Re-entry guard: the slot's action is taken once per turn (p.7). Nothing re-enters here in the
        // p.11 order - a passed-over Investor turn follows the action and ends the turn - but a re-entry
        // must not repeat it.
        if (ns.HasBuiltThisTurn) return;

        var strategy = GetStrategy(controller);
        if (strategy is RLBotStrategy && RLBotStrategy.TrainingActionOverride.Value.HasValue)
        {
            // During training, TcpTrainingServer handles Factory building directly step-by-step
            return;
        }

        if (ns.Treasury < FactoryCost)
        {
            GameLogger.LogFactoryNotAfforded(ctx, game, ns.Nation, controller.BotName ?? "Bot");
            ns.HasBuiltThisTurn = true; // Nothing to build; nothing left to decide
            return;
        }

        var homeCities = TerritoryData.AllTerritories.Where(t => t.IsHomeCity(ns.Nation)).ToList();

        // Same legality the endpoint and the RL action mask use - one definition of "buildable".
        var validCities = FactoryEngine.BuildableCities(game, ns.Nation);

        var chosenCityId = validCities.Any() ? strategy.ChooseCityForFactory(game, ns.Nation, validCities) : null;
        if (chosenCityId == null)
        {
            // Same three-way split TcpTrainingServer's wasted-Factory penalty already makes (NoMoney /
            // AllBuilt / Blocked), so the game log and the RL log describe a turn the same way.
            string botName = controller.BotName ?? "Bot";
            if (validCities.Any())
            {
                GameLogger.LogFactoryDeclined(ctx, game, ns.Nation, botName);
            }
            else if (homeCities.All(city => game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id)?.HasFactory == true))
            {
                GameLogger.LogFactoryAllCitiesBuilt(ctx, game, ns.Nation, botName);
            }
            else
            {
                // Something buildable remains, so the only thing stopping it is a hostile army standing
                // in it - which is what the validCities filter just rejected.
                GameLogger.LogFactoryCitiesBlockaded(ctx, game, ns.Nation, botName);
            }
        }
        else
        {
            var build = FactoryEngine.BuildFactory(ctx, game, chosenCityId);
            if (!build.Ok)
            {
                // chosenCityId came from BuildableCities and the treasury was checked above; a refusal is a bug.
                throw new InvalidOperationException($"Bot {controller.BotName} could not build a factory for {ns.Nation} in {chosenCityId}: {build.Error}");
            }
        }
        ns.HasBuiltThisTurn = true; // Resolved either way (built, or explicitly/implicitly skipped)
    }

    internal async Task BotProduction(ApplicationDbContext? ctx, Game game, NationState ns)
    {
        // Re-entry guard: the slot's action is taken once per turn (p.7). Nothing re-enters here in the
        // p.11 order - a passed-over Investor turn follows the action and ends the turn - but a re-entry
        // must not repeat it.
        if (ns.HasProducedThisTurn) return;

        var result = ProductionEngine.ExecuteProduction(ctx, game);
        if (!result.Ok)
        {
            // The bot is on a Production slot in its own turn; a refusal is a bug in the turn loop.
            var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
            throw new InvalidOperationException($"Bot {botName} could not produce for {ns.Nation}: {result.Error}");
        }
    }

    private async Task BotUnitActionDelay(ApplicationDbContext? ctx, Game game, int delayMs = 2000)
    {
        if (SkipDelays) return;
        if (ctx != null) await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
        await Task.Delay(delayMs);
    }

    // internal, not private, so Tests can drive a single maneuver phase directly (InternalsVisibleTo
    // in the csproj) instead of steering a whole bot turn through rondel scoring to reach it.
    internal async Task BotManeuver(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        var strategy = GetStrategy(controller);
        if (strategy is RLBotStrategy && RLBotStrategy.TrainingActionOverride.Value.HasValue)
        {
            // During training, TcpTrainingServer handles maneuver directly step-by-step
            return;
        }

        var nation = ns.Nation;
        var friendlyNations = ManeuverEngine.FriendlyNations(game, controller, nation);
        string botName = controller.BotName ?? "Bot";

        // The phase still auto-ends below, but two "auto-ended ... maneuver phase" lines do not say why
        // nothing moved. Logged once here rather than per phase, since the answer is the same for both.
        if (!game.Units.Any(u => u.Nation == nation))
        {
            GameLogger.LogManeuverNoUnits(ctx, game, nation, botName);
        }

        // Move fleets first
        var fleets = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved).ToList();
        foreach (var fleet in fleets)
        {
            if (!MapConnectivity.Adjacency.TryGetValue(fleet.TerritoryId, out var neighbors)) continue;
            var candidates = neighbors
                .Where(n => ManeuverEngine.FleetDestinationRefusal(game, fleet, n, controller) == null)
                .ToList();
            candidates.Add(fleet.TerritoryId); // Allow staying put

            var target = candidates.OrderByDescending(n => strategy.ScoreManeuverDestination(game, fleet, n, controller)).FirstOrDefault();
            if (target == null) continue;

            if (target == fleet.TerritoryId)
            {
                if (await BotStayAndDecideHostility(ctx, game, fleet, friendlyNations, nation, controller)) return;
                continue;
            }

            bool isHostileMove = DecideHostility(game, strategy, fleet, target, friendlyNations, nation);
            var move = ManeuverEngine.MoveFleet(ctx, game, fleet.Id, target, isHostileMove);
            if (!move.Ok)
            {
                throw new InvalidOperationException($"Bot {botName} chose an illegal fleet move for {nation} to {target}: {move.Error}");
            }
            if (move.BattlePending)
            {
                // Deliberately no flag placement before pausing: flags are step 3 of the maneuver
                // (Imperial-2030-Rules.pdf p.8/p.10) and this maneuver resumes once the defenders answer.
                return;
            }
            await BotUnitActionDelay(ctx, game);
        }

        ManeuverEngine.UpdateTerritoryControl(ctx, game);
        GameLogger.LogAutoEndManeuverPhase(ctx, game, "Fleets", nation, botName);
        game.CurrentManeuverPhase = ManeuverPhase.Armies;

        // Move armies
        var armies = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved).ToList();
        foreach (var army in armies)
        {
            var candidates = ManeuverHelper.GetAllReachableArmyDestinations(game, army.TerritoryId, army.Nation)
                .Select(d => d.TerritoryId)
                .ToHashSet();
            candidates.Add(army.TerritoryId); // Allow staying put

            var best = candidates.OrderByDescending(n => strategy.ScoreManeuverDestination(game, army, n, controller)).FirstOrDefault();
            if (best == null) continue;

            if (best == army.TerritoryId)
            {
                if (await BotStayAndDecideHostility(ctx, game, army, friendlyNations, nation, controller)) return;
                continue;
            }

            bool isHostileMove = DecideHostility(game, strategy, army, best, friendlyNations, nation);
            // The engine picks the way there - adjacent step, rail, or convoy over the nation's fleets -
            // by the same rule the endpoint uses, so the bot cannot play by different rules than a human.
            var move = ManeuverEngine.MoveArmy(ctx, game, army.Id, best, isHostileMove);
            if (!move.Ok)
            {
                throw new InvalidOperationException($"Bot {botName} chose an illegal army move for {nation} to {best}: {move.Error}");
            }
            if (move.BattlePending)
            {
                // See the Fleets loop above: no flag placement while the maneuver is only paused.
                return;
            }
            await BotUnitActionDelay(ctx, game);
        }

        ManeuverEngine.UpdateTerritoryControl(ctx, game);

        // Factory Destruction: Check if bot has >= 3 armies on any foreign factory
        await BotTryDestroyFactories(ctx, game, ns.Nation, controller);

        GameLogger.LogAutoEndManeuverPhase(ctx, game, "Armies", nation, botName);
        game.CurrentManeuverPhase = ManeuverPhase.None;
    }

    /// <summary>
    /// Whether the bot enters <paramref name="target"/> standing upright. The strategy decides; a nation's
    /// last unoccupied factory province may only be entered peacefully (p.10), so that overrides it.
    /// </summary>
    private static bool DecideHostility(Game game, IBotStrategy strategy, Unit unit, string target, HashSet<Nation> friendlyNations, Nation nation)
    {
        bool hasEnemy = game.Units.Any(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
        bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

        bool isHostileMove = strategy.DetermineHostility(hasEnemy, isForeignHome);
        if (isHostileMove && ManeuverEngine.MustEnterPeacefully(game, nation, target, unit.Id)) isHostileMove = false;
        return isHostileMove;
    }

    /// <summary>
    /// The unit stays where it is, and the bot decides its posture there: lie down in a friendly home
    /// province, or stand up in a foreign one - which, with foreign units present, is answered like an
    /// invasion (p.10). Returns true when the maneuver must pause for a battle negotiation.
    /// </summary>
    private async Task<bool> BotStayAndDecideHostility(ApplicationDbContext? ctx, Game game, Unit unit, HashSet<Nation> friendlyNations, Nation nation, Player controller)
    {
        var strategy = GetStrategy(controller);
        string botName = controller.BotName ?? "Bot";

        // Posture first, then the stay. The order matters to replay: it identifies the unit a
        // ToggleHostility entry refers to by "hostility differs from the logged result and not yet moved",
        // and the stay entry that follows carries the unit's hostility AFTER the toggle - so each entry
        // picks out the same unit again even when identical units share the territory.
        bool battlePending = false;
        var defT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId);
        if (defT != null && defT.Nation.HasValue)
        {
            bool isFriendlyHome = friendlyNations.Contains(defT.Nation.Value);
            if (isFriendlyHome && unit.IsHostile)
            {
                var toggled = ManeuverEngine.ToggleHostility(ctx, game, unit.Id);
                if (!toggled.Ok) throw new InvalidOperationException($"Bot {botName} could not lay down a {unit.UnitType} of {nation}: {toggled.Error}");
            }
            else if (!isFriendlyHome && !unit.IsHostile)
            {
                bool isEnemyPresent = game.Units.Any(u => u.TerritoryId == unit.TerritoryId && u.Id != unit.Id && !friendlyNations.Contains(u.Nation));
                bool wouldOccupyLastFactory = ManeuverEngine.MustEnterPeacefully(game, nation, unit.TerritoryId, unit.Id);
                if (!wouldOccupyLastFactory && strategy.DetermineHostility(isEnemyPresent, true))
                {
                    var toggled = ManeuverEngine.ToggleHostility(ctx, game, unit.Id);
                    if (!toggled.Ok) throw new InvalidOperationException($"Bot {botName} could not stand up a {unit.UnitType} of {nation}: {toggled.Error}");
                }
            }
        }

        var stay = ManeuverEngine.Stay(ctx, game, unit.Id);
        if (!stay.Ok) throw new InvalidOperationException($"Bot {botName} could not keep a {unit.UnitType} of {nation} in place: {stay.Error}");

        // Standing up where foreign units are is answered like an invasion (p.10).
        if (unit.IsHostile && game.Units.Contains(unit))
        {
            var battle = ManeuverEngine.ResolveStationaryBattle(ctx, game, unit.Id);
            if (!battle.Ok) throw new InvalidOperationException($"Bot {botName}: {battle.Error}");
            battlePending = battle.BattlePending;
        }

        if (battlePending) return true;
        await BotUnitActionDelay(ctx, game);
        return false;
    }

    public async Task BotTryDestroyFactories(ApplicationDbContext? ctx, Game game, Nation nation, Player controller)
    {
        var strategy = GetStrategy(controller);

        foreach (var territoryId in ManeuverEngine.FactoryDestructionCandidates(game, nation, controller))
        {
            // Ask strategy if we should destroy
            if (!strategy.ShouldDestroyFactory(game, nation, territoryId, controller)) continue;

            var result = ManeuverEngine.DestroyFactory(ctx, game, territoryId);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"Bot {controller.BotName} could not destroy the factory in {territoryId}: {result.Error}");
            }
        }
    }

    private async Task BotTaxation(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;

        var result = TaxationEngine.ExecuteTaxation(ctx, game);
        if (!result.Ok)
        {
            // The bot is on the Taxation slot in its own turn; a refusal is a bug in the turn loop.
            throw new InvalidOperationException($"Bot {controller.BotName} could not tax for {nation}: {result.Error}");
        }

        if (result.GameEnded)
        {
            if (ctx != null)
            {
                await game.SetWinnerNameAsync(ctx);
                ctx.Entry(game).State = EntityState.Modified;
            }
            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameEnded", game.Id);

            // Resolve INotificationService to send email
            using (var notificationScope = _scopeFactory.CreateScope())
            {
                var notificationService = notificationScope.ServiceProvider.GetRequiredService<INotificationService>();
                _ = notificationService.NotifyGameFinishedAsync(game, $"Ended by {nation} reaching {GameConstants.MaxPowerPoints} Power (Bot {controller.BotName})");
            }
        }
        // Otherwise the engine has already advanced the turn - Taxation ends the turn by itself.
    }

    internal async Task BotImport(ApplicationDbContext? ctx, Game game, NationState ns)
    {
        // Re-entry guard: the slot's action is taken once per turn (p.7). Nothing re-enters here in the
        // p.11 order - a passed-over Investor turn follows the action and ends the turn - but a re-entry
        // must not repeat it.
        if (ns.HasImportedThisTurn) return;

        var nation = ns.Nation;
        var controller = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId);
        if (controller == null)
        {
            ns.HasImportedThisTurn = true; // No controller to decide; don't get stuck on this slot
            return;
        }

        var strategy = GetStrategy(controller);
        if (strategy is RLBotStrategy && RLBotStrategy.TrainingActionOverride.Value.HasValue)
        {
            // During training, TcpTrainingServer handles Import directly step-by-step
            return;
        }

        if (ns.Treasury < ImportUnitCost)
        {
            GameLogger.LogImportNotAfforded(ctx, game, ns.Nation, controller.BotName ?? "Bot");
            ns.HasImportedThisTurn = true; // Nothing to import; nothing left to decide
            return;
        }
        int maxImport = Math.Min(GameConstants.MaxImportUnits, ns.Treasury);

        var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == nation).ToList();
        var imports = strategy.ChooseImports(game, ns, maxImport, homeTerritories)
            .Select(i => (i.Type, i.TerritoryId))
            .ToList();

        if (imports.Count == 0)
        {
            // Chose to import nothing. Still the turn's Import action, so it is closed and logged as such.
            ImportEngine.CompleteImport(ctx, game, imports);
            return;
        }

        // The strategy filtered for legality; a refusal means its view of the board disagrees with the rule.
        var result = ImportEngine.Import(ctx, game, imports);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"Bot {controller.BotName} chose an illegal import for {nation}: {result.Error}");
        }
    }

    public async Task BotInvestorAction(ApplicationDbContext? ctx, Game game, Player actor)
    {
        var controlledNations = game.NationStates.Where(ns => ns.ControllerId == actor.Id).Select(ns => ns.Nation).ToList();
        var availableBonds = game.Bonds.Where(b => b.HolderId == null).ToList();

        var bondToBuy = GetStrategy(actor).ChooseBondToBuy(game, actor, controlledNations, availableBonds);

        if (bondToBuy != null)
        {
            // Bots never trade in a bond.
            var bought = InvestorEngine.Buy(ctx, game, bondToBuy.Id, tradeInBondId: null);
            if (!bought.Ok)
            {
                // ChooseBondToBuy only offers unowned, affordable bonds; a refusal is a bug in the selection.
                throw new InvalidOperationException($"Bot {actor.BotName} chose an illegal bond purchase: {bought.Error}");
            }

            var investmentToast = Helpers.ToastBuilder.BuildInvestmentToast(
                bought.ActorName, bought.Nation, bought.BondCost, bought.TradeInCost, bought.TookControl, bought.PreviousControllerName);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", investmentToast, false);
        }
        else
        {
            var passed = InvestorEngine.Pass(ctx, game);
            if (!passed.Ok)
            {
                throw new InvalidOperationException($"Bot {actor.BotName} could not pass on investing: {passed.Error}");
            }
        }

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
    }

    public async Task HandleBotBattleResponse(ApplicationDbContext? ctx, Game game)
    {
        foreach (var defNation in game.PendingBattleDefenders.ToList())
        {
            if (string.IsNullOrEmpty(game.PendingBattleTerritoryId) || !game.PendingBattleAggressorNation.HasValue ||
                !game.Units.Any(u => u.TerritoryId == game.PendingBattleTerritoryId && u.Nation == game.PendingBattleAggressorNation.Value))
            {
                break;
            }

            var defNs = game.NationStates.FirstOrDefault(ns => ns.Nation == defNation);
            var defController = defNs?.ControllerId != null ? game.Players.FirstOrDefault(p => p.Id == defNs.ControllerId) : null;

            if (defController != null && !defController.IsBot)
            {
                // Human player must respond manually
                continue;
            }

            var pendingBattle = new PendingBattle
            {
                TerritoryId = game.PendingBattleTerritoryId ?? "",
                AggressorNation = game.PendingBattleAggressorNation ?? defNation,
                DefenderNations = game.PendingBattleDefenders.ToList()
            };
            // An ungoverned defender has nobody to call for a battle.
            bool retreat = defController == null || GetStrategy(defController).RetreatFromBattle(game, pendingBattle);

            var result = ManeuverEngine.RespondToBattle(ctx, game, defNation, fight: !retreat);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"Bot {defController?.BotName ?? GameConstants.SystemPlayerName} could not answer the battle for {defNation}: {result.Error}");
            }
            if (result.BattleClosed) break;
        }

        // No flag placement here: resolving these responses doesn't end the aggressor's maneuver, so
        // its flags aren't settled yet (Imperial-2030-Rules.pdf p.8/p.10). The maneuver resumes
        // afterwards and places them at its phase boundary.

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
        if (!SkipDelays)
        {
            await Task.Delay(BotDelayMs);
        }
    }

    // --- Helpers ---

    public async Task HandleBotSwissBankResponse(ApplicationDbContext? ctx, Game game, List<Player> botResponders)
    {
        var nation = game.PendingSwissBankForceNation;
        if (nation == null) return;

        foreach (var bot in botResponders.ToList())
        {
            // A bot forces the stop when it holds a bond of the nation - the interest is its to collect.
            bool hasBond = game.Bonds.Any(b => b.Nation == nation && b.HolderId == bot.Id);

            var result = InvestorEngine.RespondToSwissBank(ctx, game, bot, forceStop: hasBond);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"Bot {bot.BotName} could not answer the Swiss Bank question for {nation}: {result.Error}");
            }

            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", ToastBuilder.BuildSwissBankToast(result.ResponderName, result.Nation, isForceStop: result.ForcedStop), false);

            if (result.MoveResolved)
            {
                await SaveChangesAsync(ctx);
                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
                return;
            }
        }

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
    }

    private async Task<Game?> LoadGame(ApplicationDbContext? ctx, Guid gameId)
    {
        if (ctx == null) return null;
        return await ctx.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);
    }


    private async Task SaveChangesAsync(ApplicationDbContext? ctx)
    {
        if (ctx != null) await ctx.SaveChangesAsync();
    }

    private async Task<Game?> ReloadGameAsync(ApplicationDbContext? ctx, Game? game)
    {
        if (ctx == null) return game;
        if (game == null) return null;
        ctx.ChangeTracker.Clear();
        var reloaded = await LoadGame(ctx, game.Id);
        return reloaded ?? game;
    }
}
