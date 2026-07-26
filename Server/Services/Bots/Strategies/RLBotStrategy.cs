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

    public static int InvalidActionCount = 0;
    public static int TotalActionCount = 0;

    private static InferenceSession? _onnxSession;
    private static float[]? _normMean;
    private static float[]? _normVar;
    private static float _normEpsilon;

    static RLBotStrategy()
    {
        try
        {
            string basePath = AppContext.BaseDirectory;
            string onnxPath = Path.Combine(basePath, "imperial_ppo_bot.onnx");
            string jsonPath = Path.Combine(basePath, "vec_normalize.json");

            if (File.Exists(onnxPath) && File.Exists(jsonPath))
            {
                _onnxSession = new InferenceSession(onnxPath);
                var json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                _normMean = root.GetProperty("mean").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                _normVar = root.GetProperty("var").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                _normEpsilon = (float)root.GetProperty("epsilon").GetDouble();
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

        int desiredSlot = _cachedAction switch
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
        var allPlayers = game.Players.Select(p =>
        {
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

        bool isHomeProvince = def != null && def.Nation.HasValue;
        bool uncontrolled = !isHomeProvince && (ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value));
        if (uncontrolled && !hasEnemy) score += 100;

        bool notFriendlyHome = def?.Nation == null || !friendlyNations.Contains(def.Nation.Value);
        if (notFriendlyHome) score += 10;
        else if (!hasEnemy) score -= 50; // Penalize moving within friendly home territories if there is no enemy

        return score;
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            return TrainingActionOverride.Value.Value == 9; // 9 = Retreat, 8 = Fight
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
        return action == 9;
    }

    private int GetActionFromOnnx(Game game, Player controller, bool[] actionMask)
    {
        var state = GetStateVector(game, controller);

        if (_onnxSession == null || _normMean == null || _normVar == null)
        {
            // Fallback to random if model isn't loaded
            var validIndices = actionMask.Select((val, idx) => new { val, idx }).Where(x => x.val).Select(x => x.idx).ToList();
            if (validIndices.Count == 0) return 0;
            return validIndices[Random.Shared.Next(validIndices.Count)];
        }

        // Normalize state
        for (int j = 0; j < state.Length; j++)
        {
            state[j] = (state[j] - _normMean[j]) / (float)Math.Sqrt(_normVar[j] + _normEpsilon);
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

        for (int j = 0; j < 10; j++)
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
        if (bestAction < 0 || bestAction > 9 || !actionMask[bestAction])
        {
            InvalidActionCount++;
            var validIndices = actionMask.Select((val, idx) => new { val, idx }).Where(x => x.val).Select(x => x.idx).ToList();
            return validIndices.Count > 0 ? validIndices[0] : 0;
        }

        return bestAction;
    }

    private bool[] GetActionMask(Game game, Guid rlPlayerId)
    {
        bool[] mask = new bool[10];

        var rlPlayer = game.Players.FirstOrDefault(p => p.Id == rlPlayerId);
        if (rlPlayer == null) return mask;

        if (game.PendingBattleDefenders.Any())
        {
            bool rlIsDefender = game.PendingBattleDefenders.Any(def =>
                game.NationStates.Any(ns => ns.Nation == def && ns.ControllerId == rlPlayerId));

            if (rlIsDefender)
            {
                mask[8] = true; // Fight
                mask[9] = true; // Retreat
                return mask;
            }
        }

        if (game.IsInvestorTurn)
        {
            mask[7] = true;
            return mask;
        }

        var ns = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        if (ns == null || ns.ControllerId != rlPlayerId) return mask;

        mask[0] = IsSlotValid(ns, rlPlayer, 1) && ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation);
        mask[1] = IsSlotValid(ns, rlPlayer, 2) || IsSlotValid(ns, rlPlayer, 6);
        mask[2] = IsSlotValid(ns, rlPlayer, 5) && ns.Treasury >= 1;
        mask[3] = IsSlotValid(ns, rlPlayer, 3) || IsSlotValid(ns, rlPlayer, 7);
        mask[4] = mask[3];
        mask[5] = IsSlotValid(ns, rlPlayer, 0);
        mask[6] = IsSlotValid(ns, rlPlayer, 4);
        mask[7] = false;

        if (!mask.Any(m => m)) mask[5] = true;

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
