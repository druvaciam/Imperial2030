using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class RlTrainingPauseException : Exception { }

public class RLBotStrategy : BotStrategyBase
{
    public override string Name => "RL";
    public static AsyncLocal<int?> TrainingActionOverride = new AsyncLocal<int?>();
    public static bool IsTraining = false;

    public override bool DetermineHostility(bool hasEnemy, bool isForeignHome)
    {
        if (!hasEnemy && !isForeignHome) return false;
        return true; // RL bot should always blockade/attack if possible, like it did before
    }

    public static int InvalidActionCount = 0;
    public static int TotalActionCount = 0;

    private static InferenceSession? _onnxSession;

    static RLBotStrategy()
    {
        try
        {
            string basePath = AppContext.BaseDirectory;
            string onnxPath = Path.Combine(basePath, "imperial_ppo_bot.onnx");

            if (File.Exists(onnxPath))
            {
                _onnxSession = new InferenceSession(onnxPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ONNX model: {ex.Message}");
        }
    }

    private float[] _lastState = null;
    private int _cachedAction = -1;

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
            _cachedAction = TrainingActionOverride.Value.Value;
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
                var mask = GetActionMask(game, controller.Id);
                _cachedAction = GetActionFromOnnx(game, controller, mask);
            }
        }

        int distance = _cachedAction + 1; // Actions 0-5 map to distance 1-6
        int currentPos = ns.RondelPosition ?? 0;
        int targetSlot = (currentPos + distance) % 8;

        if (slot == targetSlot)
        {
            return 100; // Force this choice
        }
        return -100; // Don't pick this
    }

    private float[] GetStateVector(Game game, Player rlPlayer)
    {
        float[] state = new float[591];
        if (rlPlayer == null) return state;

        var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };

        var allPlayers = game.Players.Select(p => new
        {
            Player = p,
            Score = game.CalculateScore(p.Id)
        })
        .OrderByDescending(x => x.Player.Id == rlPlayer.Id ? 1 : 0)
        .ThenBy(x => x.Player.Id)
        .ToList();

        var sortedOpponents = allPlayers.Where(x => x.Player.Id != rlPlayer.Id).ToList();

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
                    var bond = game.Bonds.FirstOrDefault(b => b.Nation == nation && b.Cost == cost);
                    state[i++] = (bond == null || !bond.HolderId.HasValue) ? 1.0f : 0.0f; // Unowned
                    state[i++] = (bond != null && bond.HolderId == rlPlayer.Id) ? 1.0f : 0.0f; // Me
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

                var homeTerritories = Imperial2030.Shared.Constants.TerritoryData.AllTerritories.Where(x => x.Nation == nation).OrderBy(x => x.Id).ToList();
                for (int tIdx = 0; tIdx < 4; tIdx++)
                {
                    if (tIdx < homeTerritories.Count)
                    {
                        var tData = homeTerritories[tIdx];
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == tData.Id);
                        bool hasFactory = ts != null && ts.HasFactory;
                        bool isOccupied = game.Units.Any(u => u.TerritoryId == tData.Id && u.Nation != nation && u.IsHostile);

                        state[i++] = (tData.CityType == CityType.Brown) ? 1.0f : 0.0f; // Is Brown (0 means Blue)
                        state[i++] = hasFactory ? 1.0f : 0.0f; // Is Built
                        state[i++] = isOccupied ? 1.0f : 0.0f; // Occupied
                    }
                    else
                    {
                        for (int j = 0; j < 3; j++) state[i++] = 0;
                    }
                }

                state[i++] = game.TerritoryStates.Count(t => t.Controller == nation) / 15.0f;
                state[i++] = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army) / 10.0f;
                state[i++] = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet) / 10.0f;

                // Add 4 boolean flags for action validity
                bool noMoney = ns.Treasury < 5;
                bool allBuiltOrBlocked = homeTerritories.All(city =>
                {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                    if (ts != null && ts.HasFactory) return true; // Already built
                    bool blocked = game.Units.Any(u => u.TerritoryId == city.Id && u.Nation != ns.Nation && u.IsHostile);
                    return blocked; // Blocked by enemy
                });
                state[i++] = (!noMoney && !allBuiltOrBlocked) ? 1.0f : 0.0f; // Can build factory
                state[i++] = (game.Units.Any(u => u.Nation == nation)) ? 1.0f : 0.0f; // Has units for maneuver
                state[i++] = (ns.Treasury >= 1) ? 1.0f : 0.0f; // Has at least 1m (can import 1)
                state[i++] = (ns.Treasury >= 3) ? 1.0f : 0.0f; // Has at least 3m (can import 3)
            }
            else
            {
                state[i++] = 0;
                state[i++] = 0;
                state[i++] = -1.0f; // NEW: Rondel Position
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

        // Global Scoreboard (6 players * 9 floats = 54 floats)

        for (int pIdx = 0; pIdx < 6; pIdx++)
        {
            if (pIdx < allPlayers.Count)
            {
                var pData = allPlayers[pIdx];
                state[i++] = pData.Player.Id == rlPlayer.Id ? 1.0f : 0.0f;
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
                state[i++] = 0f;
                state[i++] = 0f;
                state[i++] = 0f;
                for (int nIdx = 0; nIdx < 7; nIdx++) state[i++] = 0f;
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

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            return TrainingActionOverride.Value.Value == 8; // 8 = Retreat, 7 = Fight
        }
        else if (IsTraining)
        {
            throw new RlTrainingPauseException();
        }

        var defNationToResolve = game.PendingBattleDefenders.FirstOrDefault();
        if (defNationToResolve == default) return false;

        var controllerId = game.NationStates.First(ns => ns.Nation == defNationToResolve).ControllerId;
        var controller = game.Players.First(p => p.Id == controllerId);

        var mask = GetActionMask(game, controller.Id);
        int action = GetActionFromOnnx(game, controller, mask);
        return action == 8;
    }

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            _cachedAction = TrainingActionOverride.Value.Value;
        }
        else if (IsTraining)
        {
            throw new RlTrainingPauseException();
        }
        else
        {
            var state = GetStateVector(game, actor);
            if (!IsSameState(state, _lastState))
            {
                _lastState = state;
                var mask = GetActionMask(game, actor.Id);
                _cachedAction = GetActionFromOnnx(game, actor, mask);
            }
        }

        if (_cachedAction == 63) return null; // Pass
        if (_cachedAction >= 9 && _cachedAction <= 62)
        {
            int bondIdx = _cachedAction - 9;
            int nationIdx = bondIdx / 9;
            int costIdx = bondIdx % 9;
            var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
            var bondCosts = new[] { 2, 4, 6, 9, 12, 16, 20, 25, 30 };

            var targetNation = imperial2030Nations[nationIdx];
            var targetCost = bondCosts[costIdx];

            return availableBonds.FirstOrDefault(b => b.Nation == targetNation && b.Cost == targetCost);
        }
        return null;
    }

    private int GetActionFromOnnx(Game game, Player controller, bool[] actionMask)
    {
        var state = GetStateVector(game, controller);

        if (_onnxSession == null)
        {
            // Fallback to random if model isn't loaded
            var validIndices = actionMask.Select((val, idx) => new { val, idx }).Where(x => x.val).Select(x => x.idx).ToList();
            if (validIndices.Count == 0) return 0;
            return validIndices[Random.Shared.Next(validIndices.Count)];
        }

        // Create Tensor
        var inputTensor = new DenseTensor<float>(state, new[] { 1, state.Length });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        // Run Inference
        using var results = _onnxSession.Run(inputs);
        var output = results.First().AsTensor<float>();

        // Apply Mask & ArgMax
        int bestAction = -1;
        float maxLogit = float.MinValue;

        for (int j = 0; j < 64; j++)
        {
            if (actionMask[j])
            {
                if (output[0, j] > maxLogit)
                {
                    maxLogit = output[0, j];
                    bestAction = j;
                }
            }
        }

        TotalActionCount++;
        if (bestAction < 0 || bestAction > 63 || !actionMask[bestAction])
        {
            InvalidActionCount++;
            var validIndices = actionMask.Select((val, idx) => new { val, idx }).Where(x => x.val).Select(x => x.idx).ToList();
            return validIndices.Count > 0 ? validIndices[0] : 0;
        }

        return bestAction;
    }

    private bool[] GetActionMask(Game game, Guid rlPlayerId)
    {
        bool[] mask = new bool[64];

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
            mask[63] = true; // Pass option

            var imperial2030Nations = new[] { Nation.Russia, Nation.China, Nation.India, Nation.Brazil, Nation.USA, Nation.Europe };
            var bondCosts = new[] { 2, 4, 6, 9, 12, 16, 20, 25, 30 };

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
