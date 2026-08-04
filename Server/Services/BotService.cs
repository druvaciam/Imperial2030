using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Services;

public class BotService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly IEnumerable<Bots.IBotStrategy> _botStrategies;
    public bool SkipDelays { get; set; } = false;

    public BotService(IServiceScopeFactory scopeFactory, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, IEnumerable<Bots.IBotStrategy> botStrategies)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _botStrategies = botStrategies;
    }

    public Bots.IBotStrategy GetStrategy(Player player)
    {
        var type = player.BotType ?? "Default";
        return _botStrategies.FirstOrDefault(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? _botStrategies.FirstOrDefault(s => s.Name == "Default")
               ?? new Bots.Strategies.DefaultBotStrategy(); // Fallback if not registered
    }

    public void TriggerBotTurn(Guid gameId, int delayMs = 3000)
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
                            if (!SkipDelays) await Task.Delay(2500);
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
                            if (!SkipDelays) await Task.Delay(3000); // add some delay before bot responds
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

            LogAction(ctx, game, $"moved to {GetSlotName(targetSlot)} (Cost: {cost}M)", "Move", nation, controller.BotName ?? "Bot");

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

            if (!SkipDelays) await Task.Delay(3000);

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
            if (!SkipDelays) await Task.Delay(2000);
            game = await ReloadGameAsync(ctx, game);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);

            // Advance turn
            game.AdvanceTurn();

            LogAction(ctx, game, "ended their turn", "EndTurn", nation, controller.BotName ?? "Bot");
            await SaveChangesAsync(ctx);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        }

        // Check if next turn is also a bot
        if (!SkipDelays)
        {
            await Task.Delay(3000);
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
                LogAction(ctx, game, $"built a factory in {city.Name}", "Factory", ns.Nation, controller.BotName ?? "Bot");
            }
        }
    }

    private async Task BotProduction(ApplicationDbContext? ctx, Game game, NationState ns)
    {
        var nation = ns.Nation;
        int produced = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        var locationNames = new List<string>();
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
            locationNames.Add($"{unitType} in {def.Name}");
        }
        ns.HasProducedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"produced {produced} units ({string.Join(", ", locationNames)})", "Production", nation, botName);
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
            var seaNeighbors = neighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)).ToList();
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
                            LogAction(ctx, game, $"fleet in {target} converted to friendly", "MoveFleet", nation, controller.BotName ?? "Bot");
                        }
                        else if (!isFriendlyHome && !fleet.IsHostile)
                        {
                            bool isEnemyPresent = game.Units.Any(u => u.TerritoryId == target && u.Id != fleet.Id && !friendlyNations.Contains(u.Nation));
                            if (GetStrategy(controller).DetermineHostility(isEnemyPresent, true))
                            {
                                fleet.IsHostile = true;
                                LogAction(ctx, game, $"fleet in {target} converted to hostile", "MoveFleet", nation, controller.BotName ?? "Bot");
                            }
                        }
                        else
                        {
                            LogAction(ctx, game, $"fleet stayed in {target}", "MoveFleet", nation, controller.BotName ?? "Bot");
                        }
                    }
                    else
                    {
                        LogAction(ctx, game, $"fleet stayed in {target}", "MoveFleet", nation, controller.BotName ?? "Bot");
                    }
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
                                RemoveUnit(ctx, game, fleet);
                                RemoveUnit(ctx, game, enemyFleet);
                                LogAction(ctx, game, $"fleet attacked {targetNation} in {targetName}. Both destroyed", "Battle", nation, controller.BotName ?? "Bot");
                                continue;
                            }
                        }
                        else
                        {
                            // Trigger Negotiation Phase
                            game.PendingBattleTerritoryId = target;
                            game.PendingBattleAggressorNation = nation;
                            game.PendingBattleDefenders = foreignDefenders.ToList();

                            string peaceOrHostile = isHostileMove ? "hostilely" : "peacefully";
                            LogAction(ctx, game, $"fleet moved {peaceOrHostile} to {targetName} from {originName}, awaiting response from {string.Join(", ", foreignDefenders)}", "MoveFleet", nation, controller.BotName ?? "Bot");
                            // Update territory control before pausing
                            await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");

                            // Exit BotManeuverFleets and pause the turn to await responses
                            return;
                        }
                    }
                }

                LogAction(ctx, game, $"fleet moved to {targetName} from {originName} (Hostile: {isHostileMove})", "MoveFleet", nation, controller.BotName ?? "Bot");
            }
        }

        await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");
        LogAction(ctx, game, "auto-ended Fleets maneuver phase", "NextPhase", nation, controller.BotName ?? "Bot");
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
                            LogAction(ctx, game, $"army in {best} converted to friendly", "MoveArmy", nation, controller.BotName ?? "Bot");
                        }
                        else if (!isFriendlyHome && !army.IsHostile)
                        {
                            bool isEnemyPresent = game.Units.Any(u => u.TerritoryId == best && u.Id != army.Id && !friendlyNations.Contains(u.Nation));
                            if (GetStrategy(controller).DetermineHostility(isEnemyPresent, true))
                            {
                                army.IsHostile = true;
                                LogAction(ctx, game, $"army in {best} converted to hostile", "MoveArmy", nation, controller.BotName ?? "Bot");
                            }
                        }
                        else
                        {
                            LogAction(ctx, game, $"army stayed in {best}", "MoveArmy", nation, controller.BotName ?? "Bot");
                        }
                    }
                    else
                    {
                        LogAction(ctx, game, $"army stayed in {best}", "MoveArmy", nation, controller.BotName ?? "Bot");
                    }
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
                                RemoveUnit(ctx, game, army);
                                RemoveUnit(ctx, game, enemyUnit);
                                LogAction(ctx, game, $"army attacked {targetNation} in {targetName}. Both destroyed", "Battle", nation, controller.BotName ?? "Bot");
                                continue;
                            }
                        }
                        else
                        {
                            // Trigger Negotiation Phase
                            game.PendingBattleTerritoryId = best;
                            game.PendingBattleAggressorNation = nation;
                            game.PendingBattleDefenders = foreignDefenders.ToList();

                            string peaceOrHostile = isHostileMove ? "hostilely" : "peacefully";
                            LogAction(ctx, game, $"army moved {peaceOrHostile} to {targetName} from {originName}, awaiting response from {string.Join(", ", foreignDefenders)}", "MoveArmy", nation, controller.BotName ?? "Bot");
                            // Update territory control before pausing
                            await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");

                            // Exit BotManeuver and pause the turn to await responses
                            return;
                        }
                    }
                }

                LogAction(ctx, game, $"army moved to {targetName} from {originName} (Hostile: {isHostileMove})", "MoveArmy", nation, controller.BotName ?? "Bot");
            }
        }

        await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");
        LogAction(ctx, game, "auto-ended Armies maneuver phase", "NextPhase", nation, controller.BotName ?? "Bot");
        game.CurrentManeuverPhase = ManeuverPhase.None;
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
                        tState.Controller = firstNation;

                        string msg = oldController.HasValue
                            ? $"took control of {territoryDef.Name} from {oldController.Value}"
                            : $"took control of {territoryDef.Name}";

                        LogAction(ctx, game, msg, "FlagPlacement", firstNation, botName);
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
        var result = Imperial2030.Server.Helpers.TaxationHelper.ApplyTaxation(game, ns, controller);

        int treasuryGain = ns.Treasury - oldTreasury;
        string soldiersPayStr = result.SoldiersPay > 0 ? $"-{result.SoldiersPay}" : result.SoldiersPay.ToString();
        string tGainStr = treasuryGain > 0 ? $"+{treasuryGain}" : treasuryGain.ToString();
        string bonusStr = result.Bonus > 0 ? $"+{result.Bonus}" : result.Bonus.ToString();
        string powerStr = result.PowerGain > 0 ? $"+{result.PowerGain}" : result.PowerGain.ToString();

        LogAction(ctx, game, $"collected taxes: {result.TotalTaxRevenue}M (Soldiers' Pay: {soldiersPayStr}M, Treasury Gain: {tGainStr}M, Bonus: {bonusStr}M, Power: {powerStr})", "Taxation", nation, controller.BotName ?? "Bot");

        if (ns.Power >= 25)
        {
            game.Status = GameStatus.Finished;
            if (ctx != null) ctx.Entry(game).State = EntityState.Modified;
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
        var locationNames = new List<string>();

        foreach (var import in imports)
        {
            AddUnit(ctx, game, new Unit { GameId = game.Id, Nation = nation, TerritoryId = import.TerritoryId, UnitType = import.Type, IsHostile = false });
            imported++;
            var tName = homeTerritories.FirstOrDefault(t => t.Id == import.TerritoryId)?.Name ?? import.TerritoryId;
            locationNames.Add($"{import.Type} in {tName}");
        }

        ns.Treasury -= imported;
        ns.HasImportedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"imported {imported} units ({string.Join(", ", locationNames)})", "Import", nation, botName);
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
            game.NationStates.First(ns => ns.Nation == bondToBuy.Nation).Treasury += bondToBuy.Cost;
            LogAction(ctx, game, $"bought {bondToBuy.Nation} {bondToBuy.Cost}M bond", "Investor", null, actor.BotName ?? "Bot");
            Imperial2030.Server.Controllers.GamesController.UpdateNationController(ctx, game, bondToBuy.Nation);
        }
        else
        {
            LogAction(ctx, game, "passed on buying a bond", "Investor", null, actor.BotName ?? "Bot");
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
                LogAction(ctx, game, $"{defNation} agreed to PEACE / RETREATED", "BattleResponse", defNation, defController.BotName ?? "Bot");
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
                LogAction(ctx, game, $"{defNation} chose to FIGHT", "BattleResponse", defNation, defController.BotName ?? "Bot");

                // Actual fight logic would remove units, etc. For simplicity, we just destroy 1 unit of each if they fight.
                var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == pendingBattle.TerritoryId && u.Nation == pendingBattle.AggressorNation);
                var friendlyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == pendingBattle.TerritoryId && u.Nation == defNation);
                if (enemyUnit != null && friendlyUnit != null)
                {
                    RemoveUnit(ctx, game, enemyUnit);
                    RemoveUnit(ctx, game, friendlyUnit);
                    LogAction(ctx, game, $"Battle in {pendingBattle.TerritoryId}: {defNation} vs {pendingBattle.AggressorNation}. Both destroyed 1 unit.", "Battle", defNation, "System");
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
            await Task.Delay(1500);
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
                LogAction(ctx, game, $"chose to FORCE STOP {nationState.Nation} on Investor.", "SwissBankResponse", nationState.Nation, botName);

                string controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : "Human"; // In bot service, we might not have the username loaded, but let's try to get it if we can. Actually, we can just use controller.User?.UserName??
                // To be safe without User loaded:
                controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : (ctx != null ? (ctx.Users.Where(u => u.Id == controller.UserId).Select(u => u.UserName).FirstOrDefault() ?? "Human") : "Human");

                LogAction(ctx, game, $"was forced by Swiss Bank to stop on Investor", "Move", nationState.Nation, controllerName);
                Imperial2030.Server.Controllers.GamesController.HandleInvestorPhase(ctx, game, nationState, controller, isLandedOn: true);
                
                await SaveChangesAsync(ctx);
                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
                await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("ShowToast", $"{botName} forced {nationState.Nation} to stop on Investor.", false);
                return;
            }
            else
            {
                string botName = bot.BotName ?? "Bot";
                LogAction(ctx, game, $"chose to PASS on forcing {nationState.Nation} to stop.", "SwissBankResponse", nationState.Nation, botName);

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
                    LogAction(ctx, game, $"moved to {GetSlotName(targetSlot)} (Cost: {cost}M)", "Move", nationState.Nation, controllerName);
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

    private void LogAction(ApplicationDbContext? ctx, Game game, string message, string type, Nation? nation, string playerName)
    {
        var action = new GameAction
        {
            GameId = game.Id,
            ActionType = type,
            Message = message,
            Nation = nation,
            PlayerName = playerName
        };
        game.Actions.Add(action);
        if (ctx != null) ctx.Entry(action).State = Microsoft.EntityFrameworkCore.EntityState.Added;
    }

    private string GetSlotName(int slot) => slot switch
    {
        0 => "Taxation",
        1 => "Factory",
        2 => "Production",
        3 => "Maneuver",
        4 => "Investor",
        5 => "Import",
        6 => "Production",
        7 => "Maneuver",
        _ => $"Slot {slot}"
    };

    private void AddUnit(ApplicationDbContext? ctx, Game game, Unit unit)
    {
        game.Units.Add(unit);
        if (ctx != null) ctx.Entry(unit).State = Microsoft.EntityFrameworkCore.EntityState.Added;
    }

    private void RemoveUnit(ApplicationDbContext? ctx, Game game, Unit unit)
    {
        game.Units.Remove(unit);
        if (ctx != null) ctx.Units.Remove(unit);
    }

    private void AddTerritoryState(ApplicationDbContext? ctx, Game game, TerritoryState ts)
    {
        game.TerritoryStates.Add(ts);
        if (ctx != null) ctx.Entry(ts).State = Microsoft.EntityFrameworkCore.EntityState.Added;
    }

    private void RemoveTerritoryState(ApplicationDbContext? ctx, Game game, TerritoryState ts)
    {
        game.TerritoryStates.Remove(ts);
        if (ctx != null) ctx.TerritoryStates.Remove(ts);
    }

    private async Task SaveChangesAsync(ApplicationDbContext? ctx)
    {
        if (ctx != null) await ctx.SaveChangesAsync();
    }

    private async Task<Game?> ReloadGameAsync(ApplicationDbContext? ctx, Game? game)
    {
        if (ctx == null) return game;
        if (game == null) return null;
        var reloaded = await LoadGame(ctx, game.Id);
        return reloaded ?? game;
    }
}
