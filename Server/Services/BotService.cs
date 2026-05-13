using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Services;

public class BotService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;

    public BotService(IServiceScopeFactory scopeFactory, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
    }

    public async Task TryPlayBotTurnAsync(Guid gameId)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var game = await LoadGame(ctx, gameId);
        if (game == null || game.Status != GameStatus.InProgress) return;

        // Handle bot investor phase
        if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
        {
            var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
            if (actor != null && actor.IsBot)
            {
                await BotInvestorAction(ctx, game, actor);
                await ctx.SaveChangesAsync();
                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                await Task.Delay(1500);
                game = await LoadGame(ctx, gameId);
                if (game == null) return;
            }
            else return; // human investor, wait
        }

        // Handle bot battle response
        if (game.PendingBattleDefenders.Any())
        {
            await HandleBotBattleResponse(ctx, game);
            return;
        }

        // Check if current nation is bot-controlled
        var nationState = game.NationStates.FirstOrDefault(ns => ns.Nation == game.CurrentTurnNation);
        if (nationState?.ControllerId == null) return;
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        if (controller == null || !controller.IsBot) return;

        await ExecuteBotTurn(ctx, game, nationState, controller);
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

        if (triggeredInvestor)
        {
            bool landedOn = (targetSlot == 4);
            Imperial2030.Server.Controllers.GamesController.HandleInvestorPhase(ctx, game, nationState, controller, landedOn);
        }

        LogAction(ctx, game, $"moved to {GetSlotName(targetSlot)} (Cost: {cost}M)", "Move", nation, controller.BotName ?? "Bot");

        // Init maneuver phase
        if (targetSlot == 3 || targetSlot == 7)
            game.CurrentManeuverPhase = ManeuverPhase.Fleets;
        else
            game.CurrentManeuverPhase = ManeuverPhase.None;

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        await Task.Delay(1200);

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
            await Task.Delay(1000);
            game = await LoadGame(ctx, gameId);
            if (game == null) return;
            nationState = game.NationStates.First(ns => ns.Nation == nation);

            // Advance turn
            var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList();
            int idx = nations.IndexOf(nation);
            game.CurrentTurnNation = nations[(idx + 1) % nations.Count];
            nationState.HasBuiltThisTurn = false;
            nationState.HasMovedThisTurn = false;
            nationState.HasImportedThisTurn = false;

            LogAction(ctx, game, "ended their turn", "EndTurn", nation, controller.BotName ?? "Bot");
            await ctx.SaveChangesAsync();
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        }

        // Check if next turn is also a bot
        await Task.Delay(800);
        await TryPlayBotTurnAsync(gameId);
    }

    private int ChooseRondelSlot(Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;
        int factoryCount = CountFactories(game, nation);
        int unitCount = game.Units.Count(u => u.Nation == nation);

        // First move - free placement
        if (ns.RondelPosition == null)
        {
            if (factoryCount < 4 && ns.Treasury >= 5) return 1; // Factory
            return 2; // Production
        }

        int bestSlot = -1;
        double bestScore = -999;

        for (int slot = 0; slot < 8; slot++)
        {
            if (slot == ns.RondelPosition.Value) continue;
            int dist = (slot - ns.RondelPosition.Value + 8) % 8;
            if (dist == 0) continue;

            int moveCost = 0;
            if (dist > 3)
            {
                int pf = ns.Power / 5;
                moveCost = (dist - 3) * (1 + pf);
            }
            if (moveCost > controller.Cash) continue;

            double score = ScoreSlot(slot, game, ns, controller, factoryCount, unitCount) - moveCost * 2;
            if (score > bestScore) { bestScore = score; bestSlot = slot; }
        }

        return bestSlot >= 0 ? bestSlot : ((ns.RondelPosition.Value + 1) % 8);
    }

    private double ScoreSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        return slot switch
        {
            1 => (factories < 4 && ns.Treasury >= 5) ? 25 : 0,       // Factory
            2 or 6 => factories >= 3 ? 20 : 12,                       // Production
            0 => EstimateTaxRevenue(game, ns.Nation) >= 6 ? 22 : EstimateTaxRevenue(game, ns.Nation) * 2, // Taxation
            3 or 7 => HasExpandableTargets(game, ns.Nation) ? 15 : 5, // Maneuver
            5 => (ns.Treasury >= 2 && units < 6) ? 10 : 0,           // Import
            4 => 3,                                                    // Investor
            _ => 0
        };
    }

    private int EstimateTaxRevenue(Game game, Nation nation)
    {
        int rev = 0;
        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation == nation)
            {
                bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation);
                if (!blocked) rev += 2;
            }
        }
        rev += game.TerritoryStates.Count(ts => ts.Controller == nation);
        return Math.Min(23, rev);
    }

    private bool HasExpandableTargets(Game game, Nation nation)
    {
        var myArmyTerritories = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army).Select(u => u.TerritoryId).Distinct();
        foreach (var tid in myArmyTerritories)
        {
            if (!MapConnectivity.Adjacency.TryGetValue(tid, out var neighbors)) continue;
            foreach (var n in neighbors)
            {
                var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == n);
                if (def == null || def.Type != TerritoryType.Land) continue;
                var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == n);
                if (ts == null || ts.Controller == null || ts.Controller != nation)
                {
                    bool hasEnemyArmy = game.Units.Any(u => u.TerritoryId == n && u.Nation != nation);
                    if (!hasEnemyArmy) return true;
                }
            }
        }
        return false;
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
        var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation && t.CityType != CityType.None);
        foreach (var city in homeCities)
        {
            var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
            if (ts != null && !ts.HasFactory)
            {
                ns.Treasury -= 5;
                ts.HasFactory = true;
                ns.HasBuiltThisTurn = true;
                LogAction(ctx, game, $"built a factory in {city.Name}", "Factory", ns.Nation, controller.BotName ?? "Bot");
                return;
            }
        }
    }

    private async Task BotProduction(ApplicationDbContext ctx, Game game, NationState ns)
    {
        var nation = ns.Nation;
        int produced = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation != nation) continue;
            bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
            if (blocked) continue;

            var unitType = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;
            if (unitType == UnitType.Army && currentArmies >= NationData.GetMaxArmies(nation)) continue;
            if (unitType == UnitType.Fleet && currentFleets >= NationData.GetMaxFleets(nation)) continue;

            ctx.Units.Add(new Unit { GameId = game.Id, Nation = nation, TerritoryId = ts.TerritoryId, UnitType = unitType, IsHostile = true });
            if (unitType == UnitType.Army) currentArmies++;
            else currentFleets++;
            produced++;
        }
        ns.HasProducedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"produced {produced} units", "Production", nation, botName);
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
            var seaNeighbors = neighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea));
            var target = seaNeighbors.FirstOrDefault(n => !game.Units.Any(u => u.TerritoryId == n && !friendlyNations.Contains(u.Nation)));
            if (target != null)
            {
                fleet.TerritoryId = target;
                fleet.HasMoved = true;
            }
        }

        game.CurrentManeuverPhase = ManeuverPhase.Armies;

        // Move armies
        var armies = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved).ToList();
        foreach (var army in armies)
        {
            if (!MapConnectivity.Adjacency.TryGetValue(army.TerritoryId, out var neighbors)) continue;
            var landNeighbors = neighbors.Where(n => TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Land)).ToList();

            // Prefer uncontrolled neutral territories without enemies
            var best = landNeighbors
                .Where(n => {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == n);
                    bool uncontrolled = ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value);
                    bool noEnemy = !game.Units.Any(u => u.TerritoryId == n && u.Nation != nation);
                    var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == n);
                    bool notFriendlyHome = def?.Nation == null || !friendlyNations.Contains(def.Nation.Value);
                    return uncontrolled && noEnemy && notFriendlyHome;
                })
                .FirstOrDefault();

            if (best == null)
            {
                // Try any land neighbor without friendly nation armies
                best = landNeighbors
                    .Where(n => !game.Units.Any(u => u.TerritoryId == n && friendlyNations.Contains(u.Nation)))
                    .FirstOrDefault();
            }

            if (best != null)
            {
                army.TerritoryId = best;
                army.HasMoved = true;

                // Update territory control
                var tsDest = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == best);
                var destDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == best);
                if (tsDest != null && destDef != null && !destDef.Nation.HasValue && tsDest.Controller != nation)
                {
                    tsDest.Controller = nation;
                }
            }
        }

        game.CurrentManeuverPhase = ManeuverPhase.None;
        LogAction(ctx, game, "completed maneuver", "Maneuver", nation, controller.BotName ?? "Bot");
    }

    private async Task BotTaxation(ApplicationDbContext ctx, Game game, NationState ns, Player controller)
    {
        var nation = ns.Nation;
        int factoryRevenue = 0;
        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation != nation) continue;
            bool hasHostile = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation);
            if (!hasHostile) factoryRevenue += 2;
        }
        int flagRevenue = game.TerritoryStates.Count(ts => ts.Controller == nation);
        int totalTax = Math.Min(23, factoryRevenue + flagRevenue);

        ns.Treasury += totalTax;
        int unitCount = game.Units.Count(u => u.Nation == nation);
        int soldiersPay = unitCount;
        ns.Treasury = Math.Max(0, ns.Treasury - soldiersPay);

        int bonus = totalTax >= 16 ? 5 : totalTax >= 14 ? 4 : totalTax >= 12 ? 3 : totalTax >= 10 ? 2 : totalTax >= 6 ? 1 : 0;
        bonus = Math.Min(bonus, ns.Treasury);
        ns.Treasury -= bonus;
        controller.Cash += bonus;

        int powerGain = totalTax <= 5 ? 0 : totalTax <= 7 ? 1 : totalTax <= 9 ? 2 : totalTax == 10 ? 3 :
            totalTax == 11 ? 4 : totalTax == 12 ? 5 : totalTax == 13 ? 6 : totalTax == 14 ? 7 :
            totalTax == 15 ? 8 : totalTax <= 17 ? 9 : 10;
        ns.Power = Math.Min(25, ns.Power + powerGain);
        ns.TaxChartPosition = totalTax;

        LogAction(ctx, game, $"collected taxes: {totalTax}M (Bonus: {bonus}M, Power: +{powerGain})", "Taxation", nation, controller.BotName ?? "Bot");

        if (ns.Power >= 25)
        {
            game.Status = GameStatus.Finished;
            await ctx.SaveChangesAsync();
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
            await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameEnded", game.Id);
            return;
        }

        // Taxation auto-advances turn
        var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList();
        int idx = nations.IndexOf(nation);
        game.CurrentTurnNation = nations[(idx + 1) % nations.Count];
        ns.HasBuiltThisTurn = false;
        ns.HasMovedThisTurn = false;
        ns.HasImportedThisTurn = false;
    }

    private async Task BotImport(ApplicationDbContext ctx, Game game, NationState ns)
    {
        var nation = ns.Nation;
        if (ns.Treasury < 1) return;
        int maxImport = Math.Min(3, ns.Treasury);
        int imported = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        var homeTerritories = TerritoryData.AllTerritories.Where(t => t.Nation == nation).ToList();
        foreach (var t in homeTerritories)
        {
            if (imported >= maxImport) break;
            bool hasHostile = game.Units.Any(u => u.TerritoryId == t.Id && u.Nation != nation && u.UnitType == UnitType.Army && u.IsHostile);
            if (hasHostile) continue;

            var unitType = t.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;
            // Prefer armies
            if (t.CityType != CityType.LightBlue || imported >= maxImport - 1)
                unitType = t.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;

            if (unitType == UnitType.Army && currentArmies >= NationData.GetMaxArmies(nation)) continue;
            if (unitType == UnitType.Fleet && currentFleets >= NationData.GetMaxFleets(nation)) continue;

            ctx.Units.Add(new Unit { GameId = game.Id, Nation = nation, TerritoryId = t.Id, UnitType = unitType, IsHostile = true });
            
            if (unitType == UnitType.Army) currentArmies++;
            if (unitType == UnitType.Fleet) currentFleets++;
            imported++;
        }

        ns.Treasury -= imported;
        ns.HasImportedThisTurn = true;
        var botName = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId)?.BotName ?? "Bot";
        LogAction(ctx, game, $"imported {imported} units", "Import", nation, botName);
    }

    private async Task BotInvestorAction(ApplicationDbContext ctx, Game game, Player actor)
    {
        // Try to buy cheapest bond of a nation the bot controls
        var controlledNations = game.NationStates.Where(ns => ns.ControllerId == actor.Id).Select(ns => ns.Nation).ToList();
        var availableBonds = game.Bonds.Where(b => b.HolderId == null).OrderBy(b => b.Cost).ToList();

        Bond? toBuy = availableBonds.FirstOrDefault(b => controlledNations.Contains(b.Nation) && b.Cost <= actor.Cash);
        if (toBuy == null)
            toBuy = availableBonds.FirstOrDefault(b => b.Cost <= actor.Cash);

        if (toBuy != null)
        {
            actor.Cash -= toBuy.Cost;
            toBuy.HolderId = actor.Id;
            var ns = game.NationStates.First(n => n.Nation == toBuy.Nation);
            ns.Treasury += toBuy.Cost;
            LogAction(ctx, game, $"bought {toBuy.Nation} {toBuy.Cost}M bond", "Investment", null, actor.BotName ?? "Bot");
            Imperial2030.Server.Controllers.GamesController.UpdateNationController(ctx, game, toBuy.Nation);
        }
        else
        {
            LogAction(ctx, game, "passed on investment", "Investment", null, actor.BotName ?? "Bot");
        }

        if (game.InvestorCardHolderId.HasValue)
        {
            var sorted = game.Players.OrderBy(p => p.Id).ToList();
            var idx = sorted.FindIndex(p => p.Id == game.InvestorCardHolderId.Value);
            game.InvestorCardHolderId = sorted[(idx + 1) % sorted.Count].Id;
        }
        game.IsInvestorTurn = false;
        game.ActingPlayerId = null;
    }

    private async Task HandleBotBattleResponse(ApplicationDbContext ctx, Game game)
    {
        foreach (var defNation in game.PendingBattleDefenders.ToList())
        {
            var defNs = game.NationStates.FirstOrDefault(ns => ns.Nation == defNation);
            if (defNs?.ControllerId == null) continue;
            var defController = game.Players.FirstOrDefault(p => p.Id == defNs.ControllerId);
            if (defController == null || !defController.IsBot) continue;

            // Bot always accepts peace
            game.PendingBattleDefenders.Remove(defNation);
            LogAction(ctx, game, $"{defNation} agreed to PEACE", "BattleResponse", defNation, defController.BotName ?? "Bot");
        }

        if (!game.PendingBattleDefenders.Any())
        {
            game.PendingBattleTerritoryId = null;
            game.PendingBattleAggressorNation = null;
        }

        await ctx.SaveChangesAsync();
        await _hubContext.Clients.Group(game.Id.ToString()).SendAsync("GameUpdated", game.Id);
        await Task.Delay(1000);
        await TryPlayBotTurnAsync(game.Id);
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
        0 => "Taxation", 1 => "Factory", 2 => "Production", 3 => "Maneuver",
        4 => "Investor", 5 => "Import", 6 => "Production", 7 => "Maneuver", _ => $"Slot {slot}"
    };
}
