using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using System.Text;
using System.Text.Json;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class RlTrainingPauseException : Exception { }

public class RLBotStrategy : BotStrategyBase
{
    public override string Name => "RL";
    public static AsyncLocal<int?> TrainingActionOverride = new AsyncLocal<int?>();
    public static bool IsTraining = false;

    private readonly HttpClient _httpClient = new HttpClient();
    private float[] _lastState = null;
    private int _cachedDesiredSlot = -1;
    private readonly Random _random = new Random();

    private bool IsSameState(float[] s1, float[] s2)
    {
        if (s1 == null || s2 == null || s1.Length != s2.Length) return false;
        for (int i = 0; i < s1.Length; i++)
            if (Math.Abs(s1[i] - s2[i]) > 0.0001f) return false;
        return true;
    }

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            _cachedDesiredSlot = TrainingActionOverride.Value.Value switch
            {
                0 => 1,
                1 => 2,
                2 => 5,
                3 => 3,
                4 => 3,
                5 => 0,
                6 => 4,
                _ => -1
            };
        }
        else if (IsTraining)
        {
            // If we are training but have no override, we need to pause the C# game loop and wait for Python.
            throw new RlTrainingPauseException();
        }
        else
        {
            var state = GetStateVector(game, controller);
            if (!IsSameState(state, _lastState))
            {
                _lastState = state;
                try
                {
                    var req = new { state = state };
                    var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
                    
                    var response = _httpClient.PostAsync("http://127.0.0.1:5001/predict", content).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        var result = JsonSerializer.Deserialize<JsonElement>(body);
                        int action = result.GetProperty("action").GetInt32();
                        
                        _cachedDesiredSlot = action switch
                        {
                            0 => 1,
                            1 => 2, // or 6
                            2 => 5,
                            3 => 3, // or 7
                            4 => 3, // or 7
                            5 => 0,
                            6 => 4,
                            _ => -1
                        };
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Failed to communicate with RL Python server at localhost:5001. Ensure the model server is running.", ex);
                }
            }
        }

        int desiredSlot = _cachedDesiredSlot;

        if (slot == desiredSlot || (desiredSlot == 2 && slot == 6) || (desiredSlot == 3 && slot == 7))
        {
            return 100; // Force this choice
        }
        return -100; // Don't pick this
    }

    private float[] GetStateVector(Game game, Player rlPlayer)
    {
        float[] state = new float[135]; // Increased from 122 to 135
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
                state[i++] = ns.ControllerId == rlPlayer.Id ? 1.0f : 0.0f;
                state[i++] = ns.RondelPosition ?? -1.0f; // NEW: Rondel Position

                state[i++] = game.TerritoryStates.Count(t => t.HasFactory && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == t.TerritoryId)?.Nation == nation && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).CityType == CityType.Brown);
                state[i++] = game.TerritoryStates.Count(t => t.HasFactory && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == t.TerritoryId)?.Nation == nation && Imperial2030.Shared.Constants.TerritoryData.AllTerritories.First(x => x.Id == t.TerritoryId).CityType == CityType.LightBlue);
                state[i++] = game.TerritoryStates.Count(t => t.Controller == nation);
                state[i++] = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
                state[i++] = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);
            }
            else
            {
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = -1.0f; // NEW: Rondel Position
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = 0;
            }
        }

        // 6 floats for one-hot encoding of CurrentTurnNation
        foreach (var nation in imperial2030Nations)
        {
            state[i++] = game.CurrentTurnNation == nation ? 1.0f : 0.0f;
        }

        // 6 floats for bond interest held by the RL player
        var myBonds = game.Bonds.Where(b => b.HolderId == rlPlayer.Id).ToList();
        foreach (var nation in imperial2030Nations)
        {
            state[i++] = myBonds.Where(b => b.Nation == nation).Sum(b => b.Interest);
        }

        state[i++] = rlPlayer.Cash;
        state[i++] = game.IsInvestorTurn ? 1.0f : 0.0f;

        // Global Scoreboard (6 players * 9 floats = 54 floats)
        var allPlayers = game.Players.Select(p => {
            float score = p.Cash;
            var playerBonds = game.Bonds.Where(b => b.HolderId == p.Id).ToList();
            foreach (var bond in playerBonds)
            {
                var nState = game.NationStates.FirstOrDefault(n => n.Nation == bond.Nation);
                if (nState != null)
                {
                    score += bond.Interest * (nState.Power / 5);
                }
            }
            return new { Player = p, Score = score };
        }).OrderByDescending(x => x.Score).ToList();

        for (int pIdx = 0; pIdx < 6; pIdx++)
        {
            if (pIdx < allPlayers.Count)
            {
                var pData = allPlayers[pIdx];
                state[i++] = pData.Player.Id == rlPlayer.Id ? 1.0f : 0.0f;
                state[i++] = pData.Score;
                state[i++] = pData.Player.Cash;
                foreach (var nation in imperial2030Nations)
                {
                    var ns = game.NationStates.FirstOrDefault(n => n.Nation == nation);
                    state[i++] = (ns != null && ns.ControllerId == pData.Player.Id) ? 1.0f : 0.0f;
                }
            }
            else
            {
                state[i++] = 0f;
                state[i++] = 0f;
                state[i++] = 0f;
                for (int nIdx = 0; nIdx < 6; nIdx++) state[i++] = 0f;
            }
        }

        // Pending Battle Context (13 floats)
        var defNationToResolve = game.PendingBattleDefenders.FirstOrDefault(def => 
            game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayer.Id));
        
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

        return state;
    }

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        var affordableBonds = availableBonds.Where(b => b.Cost <= actor.Cash).ToList();
        
        // 1. Try to strengthen control of own nations first
        var ownBond = affordableBonds.FirstOrDefault(b => controlledNations.Contains(b.Nation));
        if (ownBond != null) return ownBond;

        // 2. Buy bonds in the strongest nations first, then by highest yield (cost)
        return affordableBonds
            .OrderByDescending(b => game.NationStates.First(n => n.Nation == b.Nation).Power)
            .ThenByDescending(b => b.Cost)
            .FirstOrDefault();
    }

    public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
    {
        var nation = unit.Nation;
        var friendlyNations = game.NationStates
            .Where(ns => ns.ControllerId == controller.Id)
            .Select(ns => ns.Nation)
            .ToList();

        int score = Random.Shared.Next(0, 10);
        bool hasEnemy = game.Units.Any(u => u.TerritoryId == destinationId && !friendlyNations.Contains(u.Nation));
        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == destinationId);
        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destinationId);
        bool isMyHome = def != null && def.Nation == nation;

        if (hasEnemy) 
        {
            if (isMyHome)
            {
                score += 200; // High priority to free own home territory
                if (ts != null && ts.HasFactory) 
                {
                    score += 300; // Even higher priority to free factories
                }
            }
            else
            {
                score += 10; // Normal enemy
            }
        }

        bool uncontrolled = ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value);
        if (uncontrolled && !hasEnemy) score += 100;

        bool notFriendlyHome = def?.Nation == null || !friendlyNations.Contains(def.Nation.Value);
        if (notFriendlyHome) score += 10;

        return score;
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            return TrainingActionOverride.Value.Value == 9; // 9 = Retreat, 8 = Fight
        }
        return false;
    }
}
