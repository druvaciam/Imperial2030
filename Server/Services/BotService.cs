using Imperial2030.Server.Data;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly IEnumerable<Bots.IBotStrategy> _botStrategies;
    private readonly ILogger<BotService> _logger;
    public const int BotDelayMs = 5000;
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
            return _rlStrategies.GetOrAdd(type, t => new Bots.Strategies.RLBotStrategy(t));
        }

        return _botStrategies.FirstOrDefault(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? _botStrategies.FirstOrDefault(s => s.Name == "Default")
               ?? new Bots.Strategies.DefaultBotStrategy(); // Fallback if not registered
    }

    public void TriggerBotTurn(Guid gameId, int delayMs = BotDelayMs)
    {
        _ = Task.Run(async () =>
        {
            if (!SkipDelays && delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
            await TryPlayBotTurnAsync(gameId);
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _activeBotGames = new();

    public async Task TryPlayBotTurnAsync(Guid gameId, bool singleTurnOnly = false)
    {
        if (!_activeBotGames.TryAdd(gameId, true)) return;

        try
        {
            while (true)
            {
                using var scope = _scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                Game? game = await LoadGame(ctx, gameId);
                if (game == null || game.Status != GameStatus.InProgress) break;

                bool botActed = false;

                // Handle bot investor phase
                if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
                {
                    var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
                    if (actor != null && actor.IsBot)
                    {
                        try
                        {
                            await BotInvestorAction(ctx, game, actor);
                            await SaveChangesAsync(ctx);
                            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                            if (!SkipDelays) await Task.Delay(BotDelayMs);
                            botActed = true;
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
                }

                if (!botActed || singleTurnOnly) break;
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore during application shutdown or test teardown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TryPlayBotTurnAsync] ERROR: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        finally
        {
            _activeBotGames.TryRemove(gameId, out _);
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

            // Calculate cost
            int cost = 0;
            if (nationState.RondelPosition != null)
            {
                int distance = (targetSlot - nationState.RondelPosition.Value + 8) % 8;
                if (distance > 3)
                {
                    int powerFactor = nationState.Power / 5;
                    cost = (distance - 3) * (1 + powerFactor);
                }
            }

            int? oldPos = nationState.RondelPosition;

            // --- Swiss Bank Intercept ---
            bool crossingInvestor = false;
            if (oldPos != null && targetSlot != 4)
            {
                int dist = (targetSlot - oldPos.Value + 8) % 8;
                for (int i = 1; i < dist; i++)
                {
                    if ((oldPos.Value + i) % 8 == 4)
                    {
                        crossingInvestor = true;
                        break;
                    }
                }
            }

            if (crossingInvestor && game.PendingSwissBankForceNation == null)
            {
                int totalInterest = game.Bonds.Where(b => b.Nation == nation).Sum(b => b.Interest);
                if (nationState.Treasury >= totalInterest)
                {
                    var swissBankPlayers = game.Players.Where(p => !game.NationStates.Any(ns => ns.ControllerId == p.Id)).ToList();
                    if (swissBankPlayers.Any())
                    {
                        game.PendingSwissBankForceNation = nation;
                        game.PendingSwissBankForceTargetSlot = targetSlot;
                        game.PendingSwissBankResponders = swissBankPlayers.Select(p => p.Id).ToList();

                        await SaveChangesAsync(ctx);
                        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                        return; // PAUSE bot turn!
                    }
                }
            }

            // Clear pending just in case
            if (game.PendingSwissBankForceNation == nation)
            {
                game.PendingSwissBankForceNation = null;
                game.PendingSwissBankForceTargetSlot = null;
                game.PendingSwissBankResponders.Clear();
            }
            // --- End Swiss Bank Intercept ---

            controller.Cash -= cost;
            nationState.RondelPosition = targetSlot;
            game.ResetStateForNewMove(nationState);

            // Check investor pass-through
            bool triggeredInvestor = false;
            if (oldPos != null)
            {
                int dist = (targetSlot - oldPos.Value + 8) % 8;
                for (int i = 1; i <= dist; i++)
                {
                    int step = (oldPos.Value + i) % 8;
                    if (step == 4)
                    {
                        triggeredInvestor = true;
                        break;
                    }
                }
            }
            else if (targetSlot == 4)
            {
                triggeredInvestor = true;
            }

            GameLogger.LogRondelMove(ctx, game, targetSlot, oldPos, cost, nation, controller.BotName ?? "Bot");

            if (triggeredInvestor)
            {
                bool landedOn = (targetSlot == 4);
                Imperial2030.Server.Controllers.GamesController.HandleInvestorPhase(ctx, game, nationState, controller, landedOn);
            }

            // Init maneuver phase
            if (targetSlot == 3 || targetSlot == 7)
                game.CurrentManeuverPhase = ManeuverPhase.Fleets;
            else
                game.CurrentManeuverPhase = ManeuverPhase.None;

            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

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

        // Step 2: Execute slot action

        switch (targetSlot)
        {
            case 0: await BotTaxation(ctx, game, nationState, controller); break;
            case 1: await BotBuildFactory(ctx, game, nationState, controller); break;
            case 2:
            case 6: await BotProduction(ctx, game, nationState); break;
            case 3:
            case 7: await BotManeuver(ctx, game, nationState, controller); break;
            case 5: await BotImport(ctx, game, nationState); break;
            case 4: break; // Investor handled separately
        }

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        // If not taxation (which auto-advances) and not in maneuver, end turn
        if (targetSlot != 0 && game.Status == GameStatus.InProgress && game.CurrentManeuverPhase == ManeuverPhase.None)
        {
            if (!SkipDelays) await Task.Delay(BotDelayMs);
            game = await ReloadGameAsync(ctx, game);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);

            // Advance turn
            game.AdvanceTurn();

            GameLogger.LogEndTurn(ctx, game, nation, controller.BotName ?? "Bot");
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

        var candidates = new List<(int Slot, double Score)>();
        double maxScore = -999;
        int fallbackSlot = ns.RondelPosition.HasValue ? ((ns.RondelPosition.Value + 1) % 8) : 2;
        int bestSlot = fallbackSlot;

        for (int slot = 0; slot < 8; slot++)
        {
            if (ns.RondelPosition.HasValue && slot == ns.RondelPosition.Value) continue;

            int moveCost = 0;
            if (ns.RondelPosition.HasValue)
            {
                int dist = (slot - ns.RondelPosition.Value + 8) % 8;
                if (dist > 3)
                {
                    int pf = ns.Power / 5;
                    moveCost = (dist - 3) * (1 + pf);
                }
            }

            if (moveCost > controller.Cash) continue;

            // Prevent useless Import if treasury is 0
            if (slot == 4 && ns.Treasury == 0) continue;

            double score = GetStrategy(controller).ScoreRondelSlot(slot, game, ns, controller, factoryCount, unitCount) - moveCost * 2;

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

        double totalScore = candidates.Sum(c => c.Score);
        double roll = Random.Shared.NextDouble() * totalScore;

        double current = 0;
        foreach (var c in candidates)
        {
            current += c.Score;
            if (roll <= current) return c.Slot;
        }

        return candidates.Last().Slot;
    }

    private int CountFactories(Game game, Nation nation)
    {
        return game.TerritoryStates.Count(ts => ts.HasFactory &&
            TerritoryData.AllTerritories.Any(t => t.Id == ts.TerritoryId && t.Nation == nation));
    }

    // --- Slot Action Implementations ---

    private async Task BotBuildFactory(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        if (ns.Treasury < 5) return;
        var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation && t.CityType != CityType.None).ToList();

        var validCities = homeCities.Where(city =>
        {
            var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
            if (ts != null && ts.HasFactory) return false;
            bool hasHostileForeignArmy = game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != ns.Nation && u.IsHostile);
            return !hasHostileForeignArmy;
        }).ToList();

        if (validCities.Any())
        {
            var chosenCityId = GetStrategy(controller).ChooseCityForFactory(game, ns.Nation, validCities);
            if (chosenCityId != null)
            {
                var city = validCities.First(c => c.Id == chosenCityId);
                var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                if (ts == null)
                {
                    ts = new TerritoryState { TerritoryId = city.Id, GameId = game.Id };
                    AddTerritoryState(ctx, game, ts);
                }
                ns.Treasury -= 5;
                ts.HasFactory = true;
                ns.HasBuiltThisTurn = true;
                GameLogger.LogFactoryBuild(ctx, game, city.Name, ns.Nation, controller.BotName ?? "Bot");
            }
        }
    }

    private async Task BotProduction(ApplicationDbContext? ctx, Game game, NationState ns)
    {
        var nation = ns.Nation;
        int produced = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        var locationNames = new List<(UnitType UnitType, string TerritoryId)>();
        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation != nation) continue;
            bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
            if (blocked) continue;

            var unitType = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;
            if (unitType == UnitType.Army && currentArmies >= NationData.GetMaxArmies(nation)) continue;
            if (unitType == UnitType.Fleet && currentFleets >= NationData.GetMaxFleets(nation)) continue;

            AddUnit(ctx, game, new Unit { GameId = game.Id, Nation = nation, TerritoryId = ts.TerritoryId, UnitType = unitType, IsHostile = false });
            if (unitType == UnitType.Army) currentArmies++;
            else currentFleets++;
            produced++;
            locationNames.Add((unitType, ts.TerritoryId));
        }
        ns.HasProducedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        GameLogger.LogProduction(ctx, game, produced, locationNames, nation, botName);
    }

    private async Task BotUnitActionDelay(ApplicationDbContext? ctx, Game game, int delayMs = 2000)
    {
        if (SkipDelays) return;
        if (ctx != null) await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
        await Task.Delay(delayMs);
    }

    private async Task BotManeuver(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        var strategy = GetStrategy(controller);
        if (strategy is RLBotStrategy && RLBotStrategy.TrainingActionOverride.Value.HasValue)
        {
            // During training, TcpTrainingServer handles maneuver directly step-by-step
            return;
        }

        var nation = ns.Nation;
        // Find nations controlled by same bot player
        var friendlyNations = game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet();

        // Move fleets first
        var fleets = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved).ToList();
        foreach (var fleet in fleets)
        {
            if (!MapConnectivity.Adjacency.TryGetValue(fleet.TerritoryId, out var neighbors)) continue;
            var seaNeighbors = neighbors.Where(n =>
            {
                if (!TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)) return false;

                var canal = MapConnectivity.CanalLinks.FirstOrDefault(c =>
                    (c.Region1 == fleet.TerritoryId && c.Region2 == n) ||
                    (c.Region1 == n && c.Region2 == fleet.TerritoryId));

                if (canal != default)
                {
                    var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == canal.ControllerId);
                    if (tState != null && tState.Controller != null && tState.Controller != nation)
                    {
                        var canalNationState = game.NationStates.FirstOrDefault(ns => ns.Nation == tState.Controller.Value);
                        if (canalNationState == null || canalNationState.ControllerId != controller.Id)
                        {
                            return false; // Canal blocked
                        }
                    }
                }
                return true;
            }).ToList();
            seaNeighbors.Add(fleet.TerritoryId); // Allow staying put

            var target = seaNeighbors.OrderByDescending(n => GetStrategy(controller).ScoreManeuverDestination(game, fleet, n, controller)).FirstOrDefault();

            if (target != null)
            {
                if (target == fleet.TerritoryId)
                {
                    fleet.HasMoved = true;
                    var defT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
                    if (defT != null && defT.Nation.HasValue)
                    {
                        bool isFriendlyHome = friendlyNations.Contains(defT.Nation.Value);
                        if (isFriendlyHome && fleet.IsHostile)
                        {
                            fleet.IsHostile = false;
                            GameLogger.LogHostilityToggle(ctx, game, fleet.UnitType, target, fleet.IsHostile, nation, controller.BotName ?? "Bot");
                        }
                        else if (!isFriendlyHome && !fleet.IsHostile)
                        {
                            bool isEnemyPresent = game.Units.Any(u => u.TerritoryId == target && u.Id != fleet.Id && !friendlyNations.Contains(u.Nation));
                            if (GetStrategy(controller).DetermineHostility(isEnemyPresent, true))
                            {
                                fleet.IsHostile = true;
                                GameLogger.LogHostilityToggle(ctx, game, fleet.UnitType, target, fleet.IsHostile, nation, controller.BotName ?? "Bot");
                            }
                        }
                        else
                        {
                            GameLogger.LogUnitStay(ctx, game, UnitType.Fleet, target, nation, controller.BotName ?? "Bot");
                        }
                    }
                    else
                    {
                        GameLogger.LogUnitStay(ctx, game, UnitType.Fleet, target, nation, controller.BotName ?? "Bot");
                    }
                    await BotUnitActionDelay(ctx, game);
                    continue;
                }
                bool hasEnemy = game.Units.Any(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
                var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
                bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                bool isHostileMove = GetStrategy(controller).DetermineHostility(hasEnemy, isForeignHome);

                if (isHostileMove && def != null && def.Nation.HasValue && def.Nation.Value != nation)
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

                var originName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == fleet.TerritoryId)?.Name ?? fleet.TerritoryId;
                var targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target)?.Name ?? target;

                var originalTerritoryId = fleet.TerritoryId;
                fleet.TerritoryId = target;
                fleet.HasMoved = true;
                fleet.IsHostile = isHostileMove;

                if (hasEnemy)
                {
                    var foreignDefenders = game.Units
                        .Where(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation))
                        .Where(u => u.UnitType == UnitType.Fleet || (isForeignHome && def != null && u.Nation == def.Nation.Value && isHostileMove))
                        .Select(u => u.Nation)
                        .Distinct()
                        .ToList();

                    if (foreignDefenders.Any())
                    {
                        if (isHostileMove && foreignDefenders.Count == 1)
                        {
                            var targetNation = foreignDefenders.First();
                            var enemyFleet = game.Units.FirstOrDefault(u => u.TerritoryId == target && u.Nation == targetNation &&
                                (u.UnitType == UnitType.Fleet || (isForeignHome && def != null && u.Nation == def.Nation.Value)));

                            if (enemyFleet != null)
                            {
                                GameLogger.LogUnitMove(ctx, game, fleet.UnitType, originalTerritoryId, target, true, nation, controller.BotName ?? "Bot");
                                RemoveUnit(ctx, game, fleet);
                                RemoveUnit(ctx, game, enemyFleet);
                                GameLogger.LogBattleDestruction(ctx, game, fleet.UnitType, targetNation, enemyFleet.UnitType, target, nation, controller.BotName ?? "Bot");
                                await BotUnitActionDelay(ctx, game);
                                continue;
                            }
                        }
                        else
                        {
                            // Trigger Negotiation Phase
                            game.PendingBattleTerritoryId = target;
                            game.PendingBattleAggressorNation = nation;
                            game.PendingBattleDefenders = foreignDefenders.ToList();

                            GameLogger.LogUnitMoveAwaitingResponse(ctx, game, UnitType.Fleet, originalTerritoryId, target, isHostileMove, string.Join(", ", foreignDefenders), nation, controller.BotName ?? "Bot");
                            // Update territory control before pausing
                            await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");

                            // Exit BotManeuverFleets and pause the turn to await responses
                            return;
                        }
                    }
                }

                GameLogger.LogUnitMove(ctx, game, UnitType.Fleet, originalTerritoryId, target, isHostileMove, nation, controller.BotName ?? "Bot");
                await BotUnitActionDelay(ctx, game);
            }
        }

        await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");
        GameLogger.LogAutoEndManeuverPhase(ctx, game, "Fleets", nation, controller.BotName ?? "Bot");
        game.CurrentManeuverPhase = ManeuverPhase.Armies;

        // Move armies
        var armies = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved).ToList();
        foreach (var army in armies)
        {
            var destinations = Imperial2030.Server.Helpers.ManeuverHelper.GetAllReachableArmyDestinations(game, army.TerritoryId, army.Nation);
            var convoyPaths = new Dictionary<string, List<Unit>>();
            var landNeighbors = new HashSet<string>();

            foreach (var dest in destinations)
            {
                landNeighbors.Add(dest.TerritoryId);
                if (dest.IsConvoy && dest.ConvoyFleets != null)
                {
                    convoyPaths[dest.TerritoryId] = dest.ConvoyFleets;
                }
            }
            landNeighbors.Add(army.TerritoryId); // Allow staying put

            var best = landNeighbors.OrderByDescending(n => GetStrategy(controller).ScoreManeuverDestination(game, army, n, controller)).FirstOrDefault();

            if (best != null)
            {
                if (best == army.TerritoryId)
                {
                    army.HasMoved = true;
                    var defT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == best);
                    if (defT != null && defT.Nation.HasValue)
                    {
                        bool isFriendlyHome = friendlyNations.Contains(defT.Nation.Value);
                        if (isFriendlyHome && army.IsHostile)
                        {
                            army.IsHostile = false;
                            GameLogger.LogHostilityToggle(ctx, game, army.UnitType, best, army.IsHostile, nation, controller.BotName ?? "Bot");
                        }
                        else if (!isFriendlyHome && !army.IsHostile)
                        {
                            bool isEnemyPresent = game.Units.Any(u => u.TerritoryId == best && u.Id != army.Id && !friendlyNations.Contains(u.Nation));
                            if (GetStrategy(controller).DetermineHostility(isEnemyPresent, true))
                            {
                                army.IsHostile = true;
                                GameLogger.LogHostilityToggle(ctx, game, army.UnitType, best, army.IsHostile, nation, controller.BotName ?? "Bot");
                            }
                        }
                        else
                        {
                            GameLogger.LogUnitStay(ctx, game, UnitType.Army, best, nation, controller.BotName ?? "Bot");
                        }
                    }
                    else
                    {
                        GameLogger.LogUnitStay(ctx, game, UnitType.Army, best, nation, controller.BotName ?? "Bot");
                    }
                    await BotUnitActionDelay(ctx, game);
                    continue;
                }

                bool hasEnemy = game.Units.Any(u => u.TerritoryId == best && !friendlyNations.Contains(u.Nation));
                var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == best);
                bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                bool isHostileMove = GetStrategy(controller).DetermineHostility(hasEnemy, isForeignHome);

                if (isHostileMove && def != null && def.Nation.HasValue && def.Nation.Value != nation)
                {
                    var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == best);
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
                        bool isTargetOccupied = game.Units.Any(u => u.TerritoryId == best && u.Nation != defenderNation && u.IsHostile);
                        if (defenderFactoryCount <= 1 && !isTargetOccupied)
                        {
                            isHostileMove = false;
                        }
                    }
                }

                var originName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == army.TerritoryId)?.Name ?? army.TerritoryId;
                var targetName = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == best)?.Name ?? best;

                var originalTerritoryId = army.TerritoryId;
                army.TerritoryId = best;
                army.HasMoved = true;
                army.IsHostile = isHostileMove;

                if (convoyPaths.TryGetValue(best, out var usedFleets))
                {
                    foreach (var f in usedFleets)
                    {
                        f.HasConvoyed = true;
                    }
                }

                if (hasEnemy)
                {
                    var foreignDefenders = game.Units
                        .Where(u => u.TerritoryId == best && !friendlyNations.Contains(u.Nation))
                        .Where(u => u.UnitType == UnitType.Army || (isForeignHome && def != null && u.Nation == def.Nation.Value && isHostileMove))
                        .Select(u => u.Nation)
                        .Distinct()
                        .ToList();

                    if (foreignDefenders.Any())
                    {
                        if (isHostileMove && foreignDefenders.Count == 1)
                        {
                            var targetNation = foreignDefenders.First();
                            var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == best && u.Nation == targetNation &&
                                (u.UnitType == UnitType.Army || (isForeignHome && def != null && u.Nation == def.Nation.Value)));

                            if (enemyUnit != null)
                            {
                                GameLogger.LogUnitMove(ctx, game, army.UnitType, originalTerritoryId, best, true, nation, controller.BotName ?? "Bot");
                                RemoveUnit(ctx, game, army);
                                RemoveUnit(ctx, game, enemyUnit);
                                GameLogger.LogBattleDestruction(ctx, game, army.UnitType, targetNation, enemyUnit.UnitType, best, nation, controller.BotName ?? "Bot");
                                await BotUnitActionDelay(ctx, game);
                                continue;
                            }
                        }
                        else
                        {
                            // Trigger Negotiation Phase
                            game.PendingBattleTerritoryId = best;
                            game.PendingBattleAggressorNation = nation;
                            game.PendingBattleDefenders = foreignDefenders.ToList();

                            GameLogger.LogUnitMoveAwaitingResponse(ctx, game, UnitType.Army, originalTerritoryId, best, isHostileMove, string.Join(", ", foreignDefenders), nation, controller.BotName ?? "Bot");
                            // Update territory control before pausing
                            await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");

                            // Exit BotManeuver and pause the turn to await responses
                            return;
                        }
                    }
                }

                GameLogger.LogUnitMove(ctx, game, UnitType.Army, originalTerritoryId, best, isHostileMove, nation, controller.BotName ?? "Bot");
                await BotUnitActionDelay(ctx, game);
            }
        }

        await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");

        // Factory Destruction: Check if bot has >= 3 armies on any foreign factory
        await BotTryDestroyFactories(ctx, game, ns.Nation, controller);

        GameLogger.LogAutoEndManeuverPhase(ctx, game, "Armies", nation, controller.BotName ?? "Bot");
        game.CurrentManeuverPhase = ManeuverPhase.None;
    }

    public async Task BotTryDestroyFactories(ApplicationDbContext? ctx, Game game, Nation nation, Player controller)
    {
        var strategy = GetStrategy(controller);
        var friendlyNations = game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet();

        // Find territories where this nation has >= 3 armies on a foreign factory with no defenders
        var armiesByTerritory = game.Units
            .Where(u => u.Nation == nation && u.UnitType == UnitType.Army)
            .GroupBy(u => u.TerritoryId)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var group in armiesByTerritory)
        {
            var territoryId = group.Key;
            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
            if (territoryDef == null || !territoryDef.Nation.HasValue) continue;

            var defenderNation = territoryDef.Nation.Value;

            // Cannot destroy your own factory
            if (friendlyNations.Contains(defenderNation)) continue;

            // Check factory exists
            var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == territoryId);
            if (tState == null || !tState.HasFactory) continue;

            // Check no defenders present
            bool hasDefenders = game.Units.Any(u => u.TerritoryId == territoryId && u.Nation == defenderNation);
            if (hasDefenders) continue;

            // Check defender has > 1 factory (cannot destroy the last one)
            var defenderFactoryCount = game.TerritoryStates.Count(s =>
            {
                if (!s.HasFactory) return false;
                var t = TerritoryData.AllTerritories.FirstOrDefault(td => td.Id == s.TerritoryId);
                return t != null && t.Nation == defenderNation;
            });
            if (defenderFactoryCount <= 1) continue;

            // Ask strategy if we should destroy
            if (!strategy.ShouldDestroyFactory(game, nation, territoryId, controller)) continue;

            // Execute destruction: remove 3 armies and the factory
            var armiesToSacrifice = group.Take(3).ToList();
            foreach (var army in armiesToSacrifice)
            {
                RemoveUnit(ctx, game, army);
            }
            tState.HasFactory = false;

            GameLogger.LogFactoryDestruction(ctx, game, territoryId, nation, controller.BotName ?? "Bot");
        }
    }

    private async Task BotUpdateTerritoryControl(ApplicationDbContext? ctx, Game game, string botName)
    {
        var territoriesWithUnits = game.Units.Select(u => u.TerritoryId).Distinct().ToList();

        foreach (var tId in territoriesWithUnits)
        {
            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tId).ToList();
            if (!unitsInTerritory.Any()) continue;

            var firstNation = unitsInTerritory.First().Nation;
            if (unitsInTerritory.All(u => u.Nation == firstNation))
            {
                var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tId);

                if (territoryDef != null)
                {
                    var states = game.TerritoryStates.Where(ts => ts.TerritoryId == tId).ToList();
                    var tState = states.FirstOrDefault();

                    if (states.Count > 1)
                    {
                        // Clean up duplicates caused by concurrent API calls
                        for (int i = 1; i < states.Count; i++)
                        {
                            RemoveTerritoryState(ctx, game, states[i]);
                        }
                    }

                    if (tState == null)
                    {
                        tState = new TerritoryState { TerritoryId = tId, GameId = game.Id };
                        AddTerritoryState(ctx, game, tState);
                    }

                    bool isHomeProvince = territoryDef.Nation.HasValue;

                    if (!isHomeProvince && tState.Controller != firstNation)
                    {
                        var oldController = tState.Controller;
                        int flagCount = game.TerritoryStates.Count(ts => ts.Controller == firstNation);

                        if (flagCount >= 15)
                        {
                            if (oldController != null)
                            {
                                tState.Controller = null;
                                GameLogger.LogTerritoryControlChange(ctx, game, territoryDef.Name, oldController, null, botName);
                            }
                        }
                        else
                        {
                            tState.Controller = firstNation;
                            GameLogger.LogTerritoryControlChange(ctx, game, territoryDef.Name, oldController, firstNation, botName);
                        }
                    }
                }
            }
        }
    }

    private async Task BotTaxation(ApplicationDbContext? ctx, Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;
        int oldTreasury = ns.Treasury;
        // --- Apply Centralized Taxation Logic ---
        var result = TaxationHelper.ApplyTaxation(game, ns, controller);

        int treasuryGain = ns.Treasury - oldTreasury;
        GameLogger.LogTaxation(ctx, game, result.TotalTaxRevenue, result.SoldiersPay, treasuryGain, result.Bonus, result.PowerGain, nation, controller.BotName ?? "Bot");

        if (ns.Power >= 25)
        {
            game.Status = GameStatus.Finished;
            game.FinishedAt = DateTime.UtcNow;

            if (ctx != null)
            {
                await game.SetWinnerNameAsync(ctx);
                ctx.Entry(game).State = EntityState.Modified;
            }

            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameEnded", game.Id);
            return;
        }

        // Taxation auto-advances turn
        game.AdvanceTurn();
    }

    private async Task BotImport(ApplicationDbContext? ctx, Game game, NationState ns)
    {
        var nation = ns.Nation;
        if (ns.Treasury < 1) return;
        int maxImport = Math.Min(3, ns.Treasury);

        var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == nation).ToList();
        var controller = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId);
        if (controller == null) return;

        var imports = GetStrategy(controller).ChooseImports(game, ns, maxImport, homeTerritories);

        int imported = 0;
        var locationNames = new List<(UnitType UnitType, string TerritoryId)>();

        foreach (var import in imports)
        {
            AddUnit(ctx, game, new Unit { GameId = game.Id, Nation = nation, TerritoryId = import.TerritoryId, UnitType = import.Type, IsHostile = false });
            imported++;
            locationNames.Add((import.Type, import.TerritoryId));
        }

        ns.Treasury -= imported;
        ns.HasImportedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        GameLogger.LogImport(ctx, game, imported, locationNames, nation, botName);
    }

    public async Task BotInvestorAction(ApplicationDbContext? ctx, Game game, Player actor)
    {
        var controlledNations = game.NationStates.Where(ns => ns.ControllerId == actor.Id).Select(ns => ns.Nation).ToList();
        var availableBonds = game.Bonds.Where(b => b.HolderId == null).ToList();

        var bondToBuy = GetStrategy(actor).ChooseBondToBuy(game, actor, controlledNations, availableBonds);

        if (bondToBuy != null)
        {
            bondToBuy.HolderId = actor.Id;
            actor.Cash -= bondToBuy.Cost;
            var ns = game.NationStates.First(n => n.Nation == bondToBuy.Nation);
            ns.Treasury += bondToBuy.Cost;
            string botName = actor.BotName ?? "Bot";

            var oldControllerId = ns.ControllerId;
            Imperial2030.Server.Controllers.GamesController.UpdateNationController(ctx, game, bondToBuy.Nation);
            var newControllerId = ns.ControllerId;

            string? newControllerName = null;
            string? oldControllerName = null;
            bool isSwissBankKicked = false;

            if (newControllerId == actor.Id)
            {
                newControllerName = actor.GetPlayerName(ctx);
            }
            else if (newControllerId.HasValue)
            {
                var newController = game.Players.FirstOrDefault(p => p.Id == newControllerId.Value);
                if (newController != null)
                {
                    newControllerName = newController.GetPlayerName(ctx);
                }
            }

            if (oldControllerId.HasValue)
            {
                var oldController = game.Players.FirstOrDefault(p => p.Id == oldControllerId.Value);
                if (oldController != null)
                {
                    oldControllerName = oldController.GetPlayerName(ctx);
                }
            }

            if (oldControllerId != newControllerId)
            {
                if (oldControllerId.HasValue)
                {
                    var oldControllerStillControlsNations = game.NationStates.Any(n => n.ControllerId == oldControllerId.Value);
                    if (!oldControllerStillControlsNations)
                    {
                        var oldControllerEntity = game.Players.FirstOrDefault(p => p.Id == oldControllerId.Value);
                        if (oldControllerEntity != null)
                        {
                            isSwissBankKicked = true;
                        }
                    }
                }
            }

            string toastMsg = $"{botName} bought {bondToBuy.Nation} {bondToBuy.Cost}M bond";
            if (newControllerName != null && newControllerName != oldControllerName)
            {
                toastMsg += $" and took control of {bondToBuy.Nation}";
                if (oldControllerName != null)
                {
                    toastMsg += $" from {oldControllerName}";
                }
            }

            GameLogger.LogInvestmentBuy(
                ctx,
                game,
                bondToBuy.Nation,
                bondToBuy.Cost,
                botName,
                newControllerName,
                oldControllerName,
                isSwissBankKicked,
                null);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", toastMsg, false);
        }
        else
        {
            GameLogger.LogInvestmentPass(ctx, game, actor.BotName ?? "Bot");
        }

        if (game.PendingInvestorIds != null && game.PendingInvestorIds.Any())
        {
            game.ActingPlayerId = game.PendingInvestorIds[0];
            game.PendingInvestorIds = game.PendingInvestorIds.Skip(1).ToList();
        }
        else
        {
            if (game.InvestorCardHolderId.HasValue)
            {
                var sorted = game.Players.OrderBy(p => p.Id).ToList();
                var idx = sorted.FindIndex(p => p.Id == game.InvestorCardHolderId.Value);
                game.InvestorCardHolderId = sorted[(idx + 1) % sorted.Count].Id;
            }
            game.IsInvestorTurn = false;
            game.ActingPlayerId = null;
        }

        await SaveChangesAsync(ctx);
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
    }

    public async Task HandleBotBattleResponse(ApplicationDbContext? ctx, Game game)
    {
        foreach (var defNation in game.PendingBattleDefenders.ToList())
        {
            var defNs = game.NationStates.FirstOrDefault(ns => ns.Nation == defNation);
            if (defNs?.ControllerId == null) continue;
            var defController = game.Players.FirstOrDefault(p => p.Id == defNs.ControllerId);
            if (defController == null || !defController.IsBot) continue;

            var pendingBattle = new PendingBattle
            {
                TerritoryId = game.PendingBattleTerritoryId ?? "",
                AggressorNation = game.PendingBattleAggressorNation ?? defNation,
                DefenderNations = game.PendingBattleDefenders.ToList()
            };

            bool retreat = GetStrategy(defController).RetreatFromBattle(game, pendingBattle);

            if (retreat)
            {
                var defenders = game.PendingBattleDefenders.ToList();
                defenders.Remove(defNation);
                game.PendingBattleDefenders = defenders;
                if (ctx != null) ctx.Entry(game).Property(g => g.PendingBattleDefenders).IsModified = true;
                GameLogger.LogBattleResponsePeace(ctx, game, defNation, pendingBattle.AggressorNation, pendingBattle.TerritoryId, defController.BotName ?? "Bot");
            }
            else
            {
                // Fight!
                var defenders = game.PendingBattleDefenders.ToList();
                defenders.Remove(defNation); // It's no longer pending for them
                game.PendingBattleDefenders = defenders;
                if (ctx != null) ctx.Entry(game).Property(g => g.PendingBattleDefenders).IsModified = true;
                // Resolve battle...
                // The current codebase just removes them and logs peace for everyone. 
                // Let's preserve the original behavior for now if they fight (we'll just log they fought, 
                // but actually the actual battle logic needs to execute. Since original bot always accepted peace, 
                // we'll just log "fought" and let the engine handle it).
                // The battle destruction log will handle the logging

                // Actual fight logic would remove units, etc. For simplicity, we just destroy 1 unit of each if they fight.
                var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == pendingBattle.TerritoryId && u.Nation == pendingBattle.AggressorNation);
                var friendlyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == pendingBattle.TerritoryId && u.Nation == defNation);
                if (enemyUnit != null && friendlyUnit != null)
                {
                    RemoveUnit(ctx, game, enemyUnit);
                    RemoveUnit(ctx, game, friendlyUnit);
                    GameLogger.LogBattleResponseDestruction(ctx, game, defNation, friendlyUnit.UnitType, pendingBattle.AggressorNation, enemyUnit.UnitType, pendingBattle.TerritoryId, "System");
                }
            }
        }

        await BotUpdateTerritoryControl(ctx, game, "System");

        if (!game.PendingBattleDefenders.Any())
        {
            game.PendingBattleTerritoryId = null;
            game.PendingBattleAggressorNation = null;
        }

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
            bool hasBond = game.Bonds.Any(b => b.Nation == nation && b.HolderId == bot.Id);
            var request = new Imperial2030.Server.Controllers.SwissBankResponseRequest { ForceStop = hasBond };

            var nationState = game.NationStates.First(n => n.Nation == nation);
            var controller = game.Players.First(p => p.Id == nationState.ControllerId);

            if (request.ForceStop)
            {
                int targetSlot = 4;
                int? currentSlot = nationState.RondelPosition;
                int cost = 0;
                if (currentSlot != null)
                {
                    int distance = (targetSlot - currentSlot.Value + 8) % 8;
                    if (distance > 3) cost = (distance - 3) * (1 + (nationState.Power / 5));
                }

                game.PendingSwissBankForceNation = null;
                game.PendingSwissBankForceTargetSlot = null;
                game.PendingSwissBankResponders.Clear();

                controller.Cash -= cost;
                nationState.RondelPosition = targetSlot;
                game.ResetStateForNewMove(nationState);

                string botName = bot.BotName ?? "Bot";
                GameLogger.LogSwissBankForceStop(ctx, game, nationState.Nation, botName);

                string controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : (ctx != null ? (ctx.Users.Where(u => u.Id == controller.UserId).Select(u => u.UserName).FirstOrDefault() ?? "Human") : "Human");

                GameLogger.LogRondelMove(ctx, game, targetSlot, currentSlot, cost, nationState.Nation, controllerName);
                Imperial2030.Server.Controllers.GamesController.HandleInvestorPhase(ctx, game, nationState, controller, isLandedOn: true);

                await SaveChangesAsync(ctx);
                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", $"{botName} forced {nationState.Nation} to stop on Investor.", false);
                return;
            }
            else
            {
                string botName = bot.BotName ?? "Bot";
                GameLogger.LogSwissBankPass(ctx, game, nationState.Nation, botName);

                var responders = game.PendingSwissBankResponders;
                responders.Remove(bot.Id);
                game.PendingSwissBankResponders = responders.ToList();
                if (ctx != null) ctx.Entry(game).Property(g => g.PendingSwissBankResponders).IsModified = true;

                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", $"{botName} passed on forcing {nationState.Nation} to stop.", false);

                if (!responders.Any())
                {
                    int targetSlot = game.PendingSwissBankForceTargetSlot.Value;
                    int? currentSlot = nationState.RondelPosition;
                    int cost = 0;
                    if (currentSlot != null)
                    {
                        int distance = (targetSlot - currentSlot.Value + 8) % 8;
                        if (distance > 3) cost = (distance - 3) * (1 + (nationState.Power / 5));
                    }

                    game.PendingSwissBankForceNation = null;
                    game.PendingSwissBankForceTargetSlot = null;

                    controller.Cash -= cost;
                    nationState.RondelPosition = targetSlot;
                    game.ResetStateForNewMove(nationState);

                    string controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : (ctx != null ? (ctx.Users.Where(u => u.Id == controller.UserId).Select(u => u.UserName).FirstOrDefault() ?? "Human") : "Human");
                    GameLogger.LogRondelMove(ctx, game, targetSlot, currentSlot, cost, nationState.Nation, controllerName);
                    Imperial2030.Server.Controllers.GamesController.HandleInvestorPhase(ctx, game, nationState, controller, isLandedOn: false);

                    await SaveChangesAsync(ctx);
                    await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
                    return;
                }
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


    private void AddUnit(ApplicationDbContext? ctx, Game game, Unit unit)
    {
        // Add to the in-memory collection (necessary when operating on a disconnected game state like in RL training)
        game.Units.Add(unit);
        
        // Explicitly notify EF Core to track this as a new entity. 
        // Using ctx.Add() is the consistent best practice in EF Core over manually setting the EntityState.
        if (ctx != null) ctx.Add(unit);
    }

    private void RemoveUnit(ApplicationDbContext? ctx, Game game, Unit unit)
    {
        // Remove from the in-memory collection
        game.Units.Remove(unit);
        
        // Explicitly notify EF Core to mark this entity for deletion from the database.
        // Simply removing it from game.Units might just orphan the record (setting foreign key to null)
        // depending on cascade settings, so ctx.Remove() safely ensures it is actually deleted.
        if (ctx != null) ctx.Remove(unit);
    }

    private void AddTerritoryState(ApplicationDbContext? ctx, Game game, TerritoryState ts)
    {
        game.TerritoryStates.Add(ts);
        if (ctx != null) ctx.Add(ts);
    }

    private void RemoveTerritoryState(ApplicationDbContext? ctx, Game game, TerritoryState ts)
    {
        game.TerritoryStates.Remove(ts);
        if (ctx != null) ctx.Remove(ts);
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
