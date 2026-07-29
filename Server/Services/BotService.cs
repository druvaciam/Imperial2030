using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Services.Bots;
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

    private Bots.IBotStrategy GetStrategy(Player player)
    {
        var type = player.BotType ?? "Default";
        return _botStrategies.FirstOrDefault(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? _botStrategies.FirstOrDefault(s => s.Name == "Default")
               ?? new Bots.Strategies.DefaultBotStrategy(); // Fallback if not registered
    }
    public void TriggerBotTurn(Guid gameId, int delayMs = 2500)
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

                var game = await LoadGame(ctx, gameId);
                if (game == null || game.Status != GameStatus.InProgress) break;

                bool botActed = false;

                // Handle bot investor phase
                if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
                {
                    var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
                    if (actor != null && actor.IsBot)
                    {
                        await BotInvestorAction(ctx, game, actor);
                        await ctx.SaveChangesAsync();
                        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                        if (!SkipDelays) await Task.Delay(2500);
                        botActed = true;
                    }
                }
                else if (game.PendingBattleDefenders.Any())
                {
                    await HandleBotBattleResponse(ctx, game);
                    botActed = true;
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
                            catch (Imperial2030.Server.Services.Bots.Strategies.RlTrainingPauseException)
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
        finally
        {
            _activeBotGames.TryRemove(gameId, out _);
        }
    }

    private async Task ExecuteBotTurn(ApplicationDbContext ctx, Game game, NationState nationState, Player controller)
    {
        var nation = nationState.Nation;
        var gameId = game.Id;

        // Step 1: Choose rondel slot
        int targetSlot = ChooseRondelSlot(game, nationState, controller);

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
        controller.Cash -= cost;
        nationState.RondelPosition = targetSlot;
        nationState.HasMovedThisTurn = true;
        nationState.HasProducedThisTurn = false;
        nationState.HasBuiltThisTurn = false;
        nationState.HasImportedThisTurn = false;

        foreach (var u in game.Units.Where(u => u.Nation == nation))
        {
            u.HasMoved = false;
            u.HasConvoyed = false;
        }

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

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        if (!SkipDelays) await Task.Delay(2200);

        // Step 2: Execute slot action
        game = await LoadGame(ctx, gameId);
        if (game == null) return;
        nationState = game.NationStates.First(ns => ns.Nation == nation);
        controller = game.Players.First(p => p.Id == nationState.ControllerId);

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

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        // If not taxation (which auto-advances), end turn
        if (targetSlot != 0 && game.Status == GameStatus.InProgress)
        {
            if (!SkipDelays) await Task.Delay(2000);
            game = await LoadGame(ctx, gameId);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);

            // Advance turn
            game.AdvanceTurn();

            LogAction(ctx, game, "ended their turn", "EndTurn", nation, controller.BotName ?? "Bot");
            await ctx.SaveChangesAsync();
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        }

        // Check if next turn is also a bot
        if (!SkipDelays)
        {
            await Task.Delay(1800);
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

    private async Task BotBuildFactory(ApplicationDbContext ctx, Game game, NationState ns, Player controller)
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
                    game.TerritoryStates.Add(ts);
                    ctx.TerritoryStates.Add(ts);
                }
                ns.Treasury -= 5;
                ts.HasFactory = true;
                ns.HasBuiltThisTurn = true;
                LogAction(ctx, game, $"built a factory in {city.Name}", "Factory", ns.Nation, controller.BotName ?? "Bot");
            }
        }
    }

    private async Task BotProduction(ApplicationDbContext ctx, Game game, NationState ns)
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

            ctx.Units.Add(new Unit { GameId = game.Id, Nation = nation, TerritoryId = ts.TerritoryId, UnitType = unitType, IsHostile = false });
            if (unitType == UnitType.Army) currentArmies++;
            else currentFleets++;
            produced++;
            locationNames.Add($"{unitType} in {def.Name}");
        }
        ns.HasProducedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"produced {produced} units ({string.Join(", ", locationNames)})", "Production", nation, botName);
    }

    private async Task BotManeuver(ApplicationDbContext ctx, Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;
        // Find nations controlled by same bot player
        var friendlyNations = game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet();

        // Move fleets first
        var fleets = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved).ToList();
        foreach (var fleet in fleets)
        {
            if (!MapConnectivity.Adjacency.TryGetValue(fleet.TerritoryId, out var neighbors)) continue;
            var seaNeighbors = neighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)).ToList();
            var target = seaNeighbors.OrderByDescending(n => GetStrategy(controller).ScoreManeuverDestination(game, fleet, n, controller)).FirstOrDefault();

            if (target != null)
            {
                bool hasEnemy = game.Units.Any(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
                var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == target);
                bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                bool isHostileMove = false;
                if (hasEnemy) isHostileMove = true;
                else if (isForeignHome) isHostileMove = true;

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

                if (hasEnemy && isHostileMove)
                {
                    var enemyFleet = game.Units.FirstOrDefault(u => u.TerritoryId == target && !friendlyNations.Contains(u.Nation));
                    if (enemyFleet != null)
                    {
                        var enemyNation = enemyFleet.Nation;
                        ctx.Units.Remove(fleet);
                        ctx.Units.Remove(enemyFleet);
                        game.Units.Remove(fleet);
                        game.Units.Remove(enemyFleet);
                        LogAction(ctx, game, $"fleet attacked {enemyNation} in {targetName}. Both destroyed", "Battle", nation, controller.BotName ?? "Bot");
                        continue;
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
                    LogAction(ctx, game, $"army stayed in {best}", "MoveArmy", nation, controller.BotName ?? "Bot");
                    continue;
                }

                bool hasEnemy = game.Units.Any(u => u.TerritoryId == best && !friendlyNations.Contains(u.Nation));
                var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == best);
                bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);

                bool isHostileMove = false;
                if (hasEnemy) isHostileMove = true;
                else if (isForeignHome) isHostileMove = true;

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

                if (hasEnemy && isHostileMove)
                {
                    var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == best && !friendlyNations.Contains(u.Nation) &&
                        (u.UnitType == UnitType.Army || (isForeignHome && def != null && u.Nation == def.Nation.Value)));
                        
                    if (enemyUnit != null)
                    {
                        var enemyNation = enemyUnit.Nation;
                        ctx.Units.Remove(army);
                        ctx.Units.Remove(enemyUnit);
                        game.Units.Remove(army);
                        game.Units.Remove(enemyUnit);
                        LogAction(ctx, game, $"army attacked {enemyNation} in {targetName}. Both destroyed", "Battle", nation, controller.BotName ?? "Bot");
                        continue;
                    }
                }

                LogAction(ctx, game, $"army moved to {targetName} from {originName} (Hostile: {isHostileMove})", "MoveArmy", nation, controller.BotName ?? "Bot");
            }
        }

        await BotUpdateTerritoryControl(ctx, game, controller.BotName ?? "Bot");
        LogAction(ctx, game, "auto-ended Armies maneuver phase", "NextPhase", nation, controller.BotName ?? "Bot");
        game.CurrentManeuverPhase = ManeuverPhase.None;
    }

    private async Task BotUpdateTerritoryControl(ApplicationDbContext ctx, Game game, string botName)
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
                    var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == tId);

                    if (tState == null)
                    {
                        tState = new TerritoryState { TerritoryId = tId, GameId = game.Id };
                        game.TerritoryStates.Add(tState);
                        ctx.TerritoryStates.Add(tState);
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

    private async Task BotTaxation(ApplicationDbContext ctx, Game game, NationState ns, Player controller)
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
            ctx.Entry(game).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameEnded", game.Id);
            return;
        }

        // Taxation auto-advances turn
        game.AdvanceTurn();
    }

    private async Task BotImport(ApplicationDbContext ctx, Game game, NationState ns)
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
            ctx.Units.Add(new Unit { GameId = game.Id, Nation = nation, TerritoryId = import.TerritoryId, UnitType = import.Type, IsHostile = false });
            imported++;
            var tName = homeTerritories.FirstOrDefault(t => t.Id == import.TerritoryId)?.Name ?? import.TerritoryId;
            locationNames.Add($"{import.Type} in {tName}");
        }

        ns.Treasury -= imported;
        ns.HasImportedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"imported {imported} units ({string.Join(", ", locationNames)})", "Import", nation, botName);
    }

    private async Task BotInvestorAction(ApplicationDbContext ctx, Game game, Player actor)
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

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
    }

    private async Task HandleBotBattleResponse(ApplicationDbContext ctx, Game game)
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
                game.PendingBattleDefenders.Remove(defNation);
                LogAction(ctx, game, $"{defNation} agreed to PEACE / RETREATED", "BattleResponse", defNation, defController.BotName ?? "Bot");
            }
            else
            {
                // Fight!
                game.PendingBattleDefenders.Remove(defNation); // It's no longer pending for them
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
                    ctx.Units.Remove(enemyUnit);
                    ctx.Units.Remove(friendlyUnit);
                    game.Units.Remove(enemyUnit);
                    game.Units.Remove(friendlyUnit);
                    LogAction(ctx, game, $"Battle in {pendingBattle.TerritoryId}: {defNation} vs {pendingBattle.AggressorNation}. Both destroyed 1 unit.", "Battle", defNation, "System");
                }
            }
        }

        if (!game.PendingBattleDefenders.Any())
        {
            game.PendingBattleTerritoryId = null;
            game.PendingBattleAggressorNation = null;
        }

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
        if (!SkipDelays)
        {
            await Task.Delay(1500);
        }
    }

    // --- Helpers ---

    private async Task<Game?> LoadGame(ApplicationDbContext ctx, Guid gameId)
    {
        return await ctx.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);
    }

    private void LogAction(ApplicationDbContext ctx, Game game, string message, string type, Nation? nation, string playerName)
    {
        ctx.GameActions.Add(new GameAction
        {
            GameId = game.Id,
            Timestamp = DateTime.UtcNow,
            PlayerName = playerName,
            Message = message,
            ActionType = type,
            Nation = nation
        });
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
}


