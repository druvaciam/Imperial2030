using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using System.Text;
using System.Threading;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class RlTrainingPauseException : Exception { }

public class RLBotStrategy : BotStrategyBase
{
    public override string Name { get; }
    public static AsyncLocal<int?> TrainingActionOverride = new AsyncLocal<int?>();
    public static bool IsTraining = false;
    public float Temperature { get; set; } = 0.1f;

    public override bool DetermineHostility(bool hasEnemy, bool isForeignHome)
    {
        if (!hasEnemy && !isForeignHome) return false;
        return true; // RL bot should always blockade/attack if possible, like it did before
    }

    public static int InvalidActionCount = 0;
    public static int TotalActionCount = 0;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InferenceSession> _sessionCache = new();
    private InferenceSession? _onnxSession;

    public static readonly int StateSize = 3164;

    // Fixed ordered territory lists for map encoding
    public static readonly string[] HomeProvinceIds = new[]
    {
        "Moscow", "Vladivostok", "Murmansk", "Novosibirsk",
        "Beijing", "Shanghai", "Chongqing", "Urumqi",
        "NewDelhi", "Mumbai", "Kolkata", "Chennai",
        "Brasilia", "RioDeJaneiro", "Manaus", "Fortaleza",
        "NewYork", "SanFrancisco", "NewOrleans", "Chicago",
        "Berlin", "London", "Paris", "Rome"
    };

    public static readonly string[] NeutralLandIds = new[]
    {
        "Ukraine", "Korea", "Mongolia", "Kazakhstan",
        "Japan", "Turkey", "Guinea", "Quebec", "Mexico",
        "Colombia", "Afghanistan", "Alaska", "Canada", "Peru",
        "Argentina", "Iran", "North-Africa", "Nigeria", "Congo",
        "South-Africa", "East-Africa", "NearEast", "Indochina",
        "Indonesia", "Philippines", "Australia", "NewZealand"
    };

    public static readonly string[] SeaZoneIds = new[]
    {
        "MediterraneanSea", "NorthAtlantic", "GulfOfGuinea",
        "NorthPacific", "SouthPacific", "SouthAtlantic",
        "CaribbeanSea", "SeaOfJapan", "ChinaSea",
        "TasmanSea", "IndianOcean"
    };

    public static readonly string[] AllManeuverTerritories = HomeProvinceIds.Concat(NeutralLandIds).Concat(SeaZoneIds).ToArray();

    public RLBotStrategy(string botType = "RL")
    {
        Name = botType;
        try
        {
            string basePath = AppContext.BaseDirectory;
            string modelFilename = $"{botType}.onnx";
            if (botType.Equals("RL", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(basePath, modelFilename)))
            {
                modelFilename = "imperial_ppo_bot.onnx";
            }
            string onnxPath = Path.Combine(basePath, modelFilename);

            if (File.Exists(onnxPath))
            {
                _onnxSession = _sessionCache.GetOrAdd(onnxPath, path =>
                {
                    var session = new InferenceSession(path);
                    Console.WriteLine($"Loaded ONNX model: {modelFilename}");
                    return session;
                });
            }
            else
            {
                Console.WriteLine($"WARNING: ONNX model {modelFilename} not found at {onnxPath}. Inference will be disabled.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ONNX model for {botType}: {ex.Message}");
        }
    }

    private float[]? _lastState = null;
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
        else if (IsTraining && game.Name != null && game.Name.StartsWith("RL_Training_") && controller.BotName != null && controller.BotName.EndsWith("Agent"))
        {
            // If we are training but have no override, we need to pause the C# game loop and wait for Python.
            // Only pause if this player is the primary agent being trained.
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
            return 1000; // Force this choice
        }
        return -1000; // Don't pick this
    }

    private float[] GetStateVector(Game game, Player rlPlayer, string? maneuverSelectedTerritoryId = null)
    {
        float[] state = new float[StateSize];
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
                        state[i++] = (ns.ControllerId == rlPlayer.Id) ? 1.0f : 0.0f; // Owned by Me
                    }
                    else
                    {
                        for (int j = 0; j < 4; j++) state[i++] = 0;
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
                for (int j = 0; j < 16; j++) state[i++] = 0; // Territory states
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

        // New: 6 explicit penalty flags for Rondel actions 0-5
        // These directly tell the neural network if an action will cause a penalty.
        var actingNs = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
        for (int act = 0; act < 6; act++)
        {
            if (game.IsInvestorTurn || game.PendingBattleDefenders.Any() || actingNs == null || actingNs.ControllerId != rlPlayer.Id)
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
                        .Where(t => t.Nation == actingNs.Nation && t.CityType != Imperial2030.Shared.Models.CityType.None);
                    allBuiltOrBlocked = homeCities.All(city =>
                    {
                        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
                        if (ts != null && ts.HasFactory) return true;
                        return game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == Imperial2030.Shared.Models.UnitType.Army && u.Nation != actingNs.Nation && u.IsHostile);
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
            var taxPreview = Imperial2030.Server.Helpers.TaxationHelper.PreviewTaxation(game, actingNs);
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

        // === MAP ENCODING (2330 floats) ===
        EncodeMapState(game, imperial2030Nations, ref i, state);

        // === MANEUVER CONTEXT (66 floats) ===
        // 1. Maneuver Selected Territory (63 floats: 62 territories + 1 None)
        for (int idx = 0; idx < AllManeuverTerritories.Length; idx++)
        {
            state[i++] = (maneuverSelectedTerritoryId == AllManeuverTerritories[idx]) ? 1.0f : 0.0f;
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
        // 4-bit thermometer: [1,0,0,0]=1, [0,1,0,0]=2, [0,0,1,0]=3, [0,0,0,1]=4+
        state[i++] = count == 1 ? 1f : 0f;
        state[i++] = count == 2 ? 1f : 0f;
        state[i++] = count == 3 ? 1f : 0f;
        state[i++] = count >= 4 ? 1f : 0f;
    }

    private static void EncodeFlagControl(Nation? controller, Nation[] nations, float[] state, ref int i)
    {
        // 7-float one-hot: 6 nations + 1 Uncontrolled
        foreach (var n in nations)
            state[i++] = (controller.HasValue && controller.Value == n) ? 1f : 0f;
        state[i++] = !controller.HasValue ? 1f : 0f; // Uncontrolled
    }

    private void EncodeMapState(Game game, Nation[] nations, ref int i, float[] state)
    {
        // 1. Home Provinces (24 × 54 = 1296 floats)
        foreach (var tId in HomeProvinceIds)
        {
            // Armies per nation (6 × 5 = 30)
            foreach (var n in nations)
            {
                var armies = game.Units.Where(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Army).ToList();
                int armyCount = armies.Count;
                EncodeUnitCount(armyCount, state, ref i);

                // Add 1 float for IsHostile presence
                bool hasHostile = armies.Any(a => a.IsHostile);
                state[i++] = hasHostile ? 1.0f : 0.0f;
            }
            // Fleets per nation (6 × 4 = 24) — resting in shipyards
            foreach (var n in nations)
            {
                int fleetCount = game.Units.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Fleet);
                EncodeUnitCount(fleetCount, state, ref i);
            }
        }

        // 2. Neutral Land Territories (27/28 × 31 = 837/868 floats)
        foreach (var tId in NeutralLandIds)
        {
            // Armies per nation (6 × 4 = 24)
            foreach (var n in nations)
            {
                int armyCount = game.Units.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Army);
                EncodeUnitCount(armyCount, state, ref i);
            }
            // Flag control (7)
            var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == tId);
            Nation? controller = ts?.Controller;
            EncodeFlagControl(controller, nations, state, ref i);
        }

        // 3. Sea Zones (11 × 31 = 341 floats)
        foreach (var tId in SeaZoneIds)
        {
            // Fleets per nation (6 × 4 = 24)
            foreach (var n in nations)
            {
                int fleetCount = game.Units.Count(u => u.TerritoryId == tId && u.Nation == n && u.UnitType == UnitType.Fleet);
                EncodeUnitCount(fleetCount, state, ref i);
            }
            // Flag control (7)
            var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == tId);
            Nation? controller = ts?.Controller;
            EncodeFlagControl(controller, nations, state, ref i);
        }
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        var defNationToResolve = game.PendingBattleDefenders.FirstOrDefault();
        if (defNationToResolve == default) return false;

        var controllerId = game.NationStates.First(ns => ns.Nation == defNationToResolve).ControllerId;
        var controller = game.Players.First(p => p.Id == controllerId);

        if (TrainingActionOverride.Value.HasValue)
        {
            return TrainingActionOverride.Value.Value == 8; // 8 = Retreat, 7 = Fight
        }
        else if (IsTraining && game.Name != null && game.Name.StartsWith("RL_Training_") && controller.BotName != null && controller.BotName.EndsWith("Agent"))
        {
            throw new RlTrainingPauseException();
        }

        var mask = GetActionMask(game, controller.Id);
        int action = GetActionFromOnnx(game, controller, mask);
        return action == 8;
    }

    private ThreadLocal<(Guid UnitId, int ChosenAction)> _maneuverCache = new ThreadLocal<(Guid, int)>();

    public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
    {
        if (_onnxSession == null)
        {
            return base.ScoreManeuverDestination(game, unit, destinationId, controller);
        }

        var cache = _maneuverCache.Value;
        if (cache.UnitId != unit.Id || cache.ChosenAction == 0)
        {
            bool[] mask = new bool[189];
            mask[126] = true; // Do Not Move

            if (unit.UnitType == UnitType.Fleet)
            {
                if (MapConnectivity.Adjacency.TryGetValue(unit.TerritoryId, out var neighbors))
                {
                    var validNeighbors = neighbors.Where(n =>
                    {
                        if (!TerritoryData.AllTerritories.Any(t => t.Id == n && t.Type == TerritoryType.Sea)) return false;

                        var canal = MapConnectivity.CanalLinks.FirstOrDefault(c =>
                            (c.Region1 == unit.TerritoryId && c.Region2 == n) ||
                            (c.Region1 == n && c.Region2 == unit.TerritoryId));

                        if (canal != default)
                        {
                            var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == canal.ControllerId);
                            if (tState != null && tState.Controller != null && tState.Controller != unit.Nation)
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

                    foreach (var dest in validNeighbors)
                    {
                        int mIdx = Array.IndexOf(AllManeuverTerritories, dest);
                        if (mIdx >= 0) mask[127 + mIdx] = true;
                    }
                }
            }
            else
            {
                var destinations = Imperial2030.Server.Helpers.ManeuverHelper.GetAllReachableArmyDestinations(game, unit.TerritoryId, unit.Nation);
                foreach (var dest in destinations)
                {
                    int mIdx = Array.IndexOf(AllManeuverTerritories, dest.TerritoryId);
                    if (mIdx >= 0) mask[127 + mIdx] = true;
                }
            }

            int chosenAction = GetActionFromOnnx(game, controller, mask, unit.TerritoryId);
            cache = (unit.Id, chosenAction);
            _maneuverCache.Value = cache;
        }

        int thisAction = 126;
        if (destinationId != unit.TerritoryId)
        {
            int idx = Array.IndexOf(AllManeuverTerritories, destinationId);
            if (idx >= 0) thisAction = 127 + idx;
        }

        if (thisAction == cache.ChosenAction)
        {
            return 1000;
        }

        return -1000;
    }

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        if (TrainingActionOverride.Value.HasValue)
        {
            _cachedAction = TrainingActionOverride.Value.Value;
        }
        else if (IsTraining && game.Name != null && game.Name.StartsWith("RL_Training_") && actor.BotName != null && actor.BotName.EndsWith("Agent"))
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

    private int GetActionFromOnnx(Game game, Player controller, bool[] actionMask, string? maneuverSelectedTerritoryId = null)
    {
        var state = GetStateVector(game, controller, maneuverSelectedTerritoryId);

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

        // Apply Mask & Action Selection
        int bestAction = -1;

        if (Temperature <= 0.001f)
        {
            float maxLogit = float.MinValue;
            for (int j = 0; j < actionMask.Length; j++)
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
        }
        else
        {
            var validLogits = new List<(int Index, float Logit)>();
            for (int j = 0; j < actionMask.Length; j++)
            {
                if (actionMask[j]) validLogits.Add((j, output[0, j]));
            }

            if (validLogits.Count > 0)
            {
                float maxValidLogit = validLogits.Max(x => x.Logit);
                var exps = validLogits.Select(x => (float)Math.Exp((x.Logit - maxValidLogit) / Temperature)).ToList();
                float sumExp = exps.Sum();
                float randomVal = (float)Random.Shared.NextDouble() * sumExp;

                float cumulative = 0;
                for (int i = 0; i < validLogits.Count; i++)
                {
                    cumulative += exps[i];
                    if (randomVal <= cumulative)
                    {
                        bestAction = validLogits[i].Index;
                        break;
                    }
                }
                if (bestAction == -1) bestAction = validLogits.Last().Index;
            }
        }

        TotalActionCount++;
        if (bestAction < 0 || bestAction >= actionMask.Length || !actionMask[bestAction])
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
