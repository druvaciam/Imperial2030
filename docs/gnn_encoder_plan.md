# Graph encoder for the Imperial 2030 RL bot (RL-G)

**Status: design only. Do not start until RL-4 has trained and been evaluated.**

Extracted from `rl4_training_analysis.md` step 7, which deliberately puts this last: steps 1-5 change the
reward landscape, and doing both at once makes neither attributable. This document exists so that when the
work does start it is transcription rather than design.

Everything below was verified against the code and `Imperial-2030-Rules.pdf` on 2026-08-30. Counts and line
references are measured, not estimated. Re-verify before implementing — the repo will have moved.

---

## 1. The problem this solves

`GetStateVector` (`Server/Services/TcpTrainingServer.cs`) emits a flat 3,172-float vector consumed by an
SB3 `MlpPolicy` with `net_arch=dict(pi=[1024, 512], vf=[1024, 512, 256])`. **There is no convolution and no
attention anywhere in the project** — and, more importantly, **the map's adjacency is not in the state at
all**. `GetStateVector` never references `MapConnectivity`. Territories enter as fixed positional slots
(per-nation home-city blocks, then aggregates like flag counts and unit totals).

So the network is never told that Kolkata borders Chongqing — a direct border between an Indian and a
Chinese home city.

**What that does NOT mean.** An earlier draft of this section claimed the agent therefore cannot act on
reachability. That was wrong, and worth stating plainly because it is the obvious inference and it
overstates the case. The action mask already supplies legality at the moment of choice: at maneuver
stage 2 only reachable destinations are unmasked, so the agent can never make an illegal move and never
has to learn adjacency in order to avoid one. Verified in the installed library —
`sb3_contrib/common/maskable/policies.py` applies the mask to the distribution *after* the forward pass
(`distribution.apply_masking(action_masks)`), so the mask is not a network input, but that only means the
network cannot condition its *preferences* on it. Ranking one legal destination above another needs
nothing more than the state, which does carry both the selected unit (a one-hot over 62 territories) and
per-territory occupancy.

**What it does mean**, and this is the actual argument for a graph encoder:

**No generalisation across territory pairs.** Because reachability is a mask rather than a feature, every
association has to be memorised separately for each (origin, destination) pair — learning "relieve my
blockaded home city from Korea" transfers nothing to the identical situation one province over, though it
is the same concept. That is 62 origins with no weight sharing between them, which makes such behaviour
slow to learn rather than impossible.

**No derived spatial features at all.** This is the sharper half. Anything that is a property of the
*board* rather than of action legality is simply absent, and the mask offers nothing there: how many
enemy armies can reach this territory next turn, whether a factory is defensible, how far a convoy
extends. Concretely, `TcpTrainingServer.IsRedundantStackMove` has to compute "enemy armies able to reach
this region" by walking `GetAllReachableArmyDestinations` for every enemy army on the board, because the
policy cannot see it — a hand-written feature standing in for something message passing would produce for
free, and only for the one case somebody noticed.

A CNN is the wrong tool: it is foundational in the AlphaZero lineage because Go/chess/shogi are played on
regular grids where a kernel can slide. This map is an irregular graph with sea regions and canals. Message
passing over the actual adjacency is the graph-shaped analogue of what convolution does for a grid.

---

## 2. The map as a graph (measured)

From `Shared/Constants/TerritoryData.cs` and `Shared/Constants/MapConnectivity.cs`:

| | count |
|---|---:|
| — home cities (6 nations x 4) | 24 |
| — sea regions (`TerritoryType.Sea`) | 11 |
| — neutral land regions | 27 |
| Territories (graph nodes) | **62** |
| Undirected edges | **159** (318 directed entries) |
| Graph symmetry | perfect — 0 asymmetric pairs |
| Max degree | 15 (`IndianOcean`) |
| Mean degree | 5.13 |

**Use `N = 62` and reuse the ordering that already exists.** Node row *k* must be
`RLBotStrategy.AllManeuverTerritories[k]` — do not invent a fresh ordering, and in particular do not sort
alphabetically.

That array is `HomeProvinceIds.Concat(NeutralLandIds).Concat(SeaZoneIds)` (`RLBotStrategy.cs:88`), and
three things already line up with it:

- **The action space.** Maneuver destination action `127 + k` is `AllManeuverTerritories[k]`. Adopting the
  same order makes node row *k* and destination action `127 + k` the same territory, so the encoder's
  per-node output sits directly opposite the action it informs. A separate ordering would require a
  permutation table between them, which is a silent-corruption bug waiting to happen.
- **The existing state vector.** `GetStateVector`'s map encoding emits those same three arrays in that
  same order, so a node-feature block can be transcribed from the current per-territory code without
  re-deriving which territory is which.
- **The maneuver-selected one-hot**, which already indexes `AllManeuverTerritories` directly.

None of this buys anything for the current MLP — a fully-connected network has no positional inductive
bias, so aligned indices are just unrelated parameters and shuffling the order would train identically.
It is precisely the graph encoder that turns the alignment into something real, because a GNN applies the
*same* weights to every node: territory identity becomes a row rather than a position in a flat vector.

Two properties to preserve. The array is a hand-written literal, so it is stable against edits to
`TerritoryData` in a way a declaration-order scan would not be — but it is also not automatically in sync
with the map. Assert both in `MapGraph`'s tests: that its contents equal `MapConnectivity.Adjacency.Keys`
as a set, and that its length is `NodeCount`. That way a territory added to the map without being added
here fails loudly instead of producing a graph with a hole in it.

Note also that the current layout could not be reused as-is even with matching order: per-territory
stride is 54 floats for home provinces and 31 for neutral land and seas, so there is no uniform "row" to
slice. `[1, 62, F_node]` is what supplies that.

### Why this makes ONNX export easy

**The graph is static.** No edge is ever added or removed during a game; `MapConnectivity.Adjacency` is a
compile-time `Dictionary<string, List<string>>`. So the normalised adjacency `A_hat` is a **constant**, and
a GCN layer is:

```
H' = activation( A_hat @ H @ W + b )
```

which is `MatMul -> MatMul -> Add -> Relu` in ONNX — the same op set ONNX Runtime already executes for
RL-3's MLP. No scatter, no gather, no dynamic shapes, no custom kernels, no new C# dependency.

> **Hard constraint: do not use PyTorch Geometric.** `MessagePassing` depends on `torch_scatter`, whose ONNX
> export is unreliable, and its whole design assumes variable-sized edge lists — which this game does not
> have. Write the layers as plain dense `torch` ops against a registered-buffer adjacency. This is cheap to
> honour up front and expensive to retrofit.

Precompute `A_hat` once as symmetric-normalised adjacency with self-loops
(`A_hat = D^-1/2 (A + I) D^-1/2`), the standard GCN normalisation. Register it with
`self.register_buffer("A_hat", ...)` so it is serialised into the graph as a constant initializer rather
than passed as an input.

---

## 3. Architecture: a separate strategy, not a branch

This is the part most likely to go wrong, so it is specified before the model.

### 3.1 The trap

`BotService.GetStrategy` (`Server/Services/BotService.cs:33`) dispatches on a **name prefix**:

```csharp
if (type.StartsWith("RL", StringComparison.OrdinalIgnoreCase))
    return _rlStrategies.GetOrAdd(key, _ => new Bots.Strategies.RLBotStrategy(type, _logger));
```

and **four other sites test the concrete type**:

| line | check | what it gates |
|---|---|---|
| `BotService.cs:476` | `!(strategy is RLBotStrategy)` | lets an RL bot choose Investor at zero treasury; heuristics skip it |
| `BotService.cs:526` | `strategy is RLBotStrategy && TrainingActionOverride...` | training drives Factory build step-by-step |
| `BotService.cs:606` | same | training drives Maneuver step-by-step |
| `BotService.cs:1158` | same | training drives Import step-by-step |

`ClearStrategyCache` (`BotService.cs:60`) also keys off `StartsWith("RL")`.

**A new `GnnBotStrategy` that does not derive from `RLBotStrategy` silently fails all five.** Nothing throws.
The bot would skip Investor at zero treasury, and — far worse — during training the three
`TrainingActionOverride` gates would not fire, so `TcpTrainingServer` would think it was driving the agent
step-by-step while `BotService` quietly ran its own heuristic fallbacks. That produces a policy trained
against actions it never actually took. It would look like a mysterious reward regression, not a wiring bug.

### 3.2 The fix: extract a base, do not add `||`

Do **not** write `strategy is RLBotStrategy || strategy is GnnBotStrategy`. That is four more places to
forget next time.

```
BotStrategyBase                       (exists)
  └── NeuralBotStrategyBase           (NEW - everything not encoder-specific)
        ├── RLBotStrategy             (exists, flat 3172-float encoder)
        └── GnnBotStrategy            (NEW, graph encoder)
```

`NeuralBotStrategyBase` owns:

- `public static AsyncLocal<int?> TrainingActionOverride` — **moved up from `RLBotStrategy:18`**. It is a
  single process-wide channel; two copies would mean the training server sets one and the other strategy
  reads the wrong one.
- The action-space constants (`FightAction`, `RetreatAction`, `FactoryDestroyAction`, `FactoryKeepAction`,
  `ImportStopAction`, `ImportPlaceActionBase/Count`, `FactorySkipAction`, `FactoryBuildActionBase/Count`,
  `TotalActionSize = 205`, `MaxImportUnits`). **The action space is shared and unchanged.** This work
  changes only how the state is *encoded*, never what the agent can *do*.
- ONNX session loading and the `_sessionCache` (`RLBotStrategy:31`), including `IntraOpNumThreads = 1` /
  `InterOpNumThreads = 1`.
- Action masking, the per-decision caches (`_lastState`, `_cachedAction`, `_maneuverCache`), and every
  `IBotStrategy` member whose logic is about *decisions*, not *encoding*.
- Two abstract members, which are the entire difference:
  ```csharp
  protected abstract IReadOnlyList<NamedOnnxValue> BuildInputs(Game game, Guid rlPlayerId);
  protected abstract bool IsCompatibleWith(InferenceSession session);
  ```

Then the five sites become `strategy is NeuralBotStrategyBase` /
`NeuralBotStrategyBase.TrainingActionOverride`. Mechanical, and it is the only edit to `BotService`.

> Keep this refactor as its **own commit with no behavioural change**, verified by the full suite passing
> before `GnnBotStrategy` is added. If a regression appears later, that ordering makes it trivially
> bisectable.

### 3.3 Naming and dispatch

Keep the `StartsWith("RL")` dispatch — the graph bot is still an RL bot, and changing that prefix would
break `ClearStrategyCache` and every existing `BotType` string in the database. Select the subclass by an
explicit set, not by string shape:

```csharp
private static readonly HashSet<string> GraphBotTypes =
    new(StringComparer.OrdinalIgnoreCase) { "RL-G", "RL-G2" };
```

A `HashSet` beats a prefix rule here: `"RL-G".StartsWith(...)` collides with any future `"RL-Greedy"`, and
an explicit set fails loudly (falls back to the flat strategy, whose input guard then rejects the model)
rather than silently feeding a graph model to the flat encoder.

---

## 4. Feature layout

Two inputs. Do not try to fold the globals into the node matrix.

### 4.1 Node features — `[1, 62, F_node]`

Per territory, in the fixed ordering from §2. Every value normalised to roughly `[-1, 1]`, matching the
existing convention (`ns.Power / 25.0f`, `ns.Treasury / 30.0f`) — `VecNormalize` runs with
`norm_obs=False` because the C# side normalises, and that stays true here.

| block | width | contents |
|---|---:|---|
| terrain | 3 | one-hot: home city / neutral land / sea |
| city type | 3 | one-hot: none / brown / light blue |
| home nation | 7 | one-hot over 6 nations + "none" |
| factory | 2 | `HasFactory`, `is blockaded` (hostile foreign army present) |
| flag/control | 7 | one-hot `TerritoryState.Controller` over 6 nations + "none" |
| units by nation | 12 | armies and fleets per nation, each `count / 8.0` |
| hostility | 2 | any hostile unit present; any hostile unit belonging to the acting nation's rivals |
| ego markers | 3 | is acting nation's home; is controlled by the RL player's nation; acting nation can build here now |

`F_node = 39`. Not load-bearing — add to the end if more is needed, same append-only discipline as the flat
vector.

**Egocentric rotation is free here and must be used.** `rl4_training_analysis.md` §2 notes the flat encoder
gives each nation a fixed block with no weight sharing, and that rotating it is principled but was *not* the
cause of the measured failure. In a graph encoder you get it for nothing: rotate the 6-wide nation one-hots
so index 0 is always the acting nation. One `(index - actingNation + 6) % 6`, applied consistently on both
sides of the socket.

### 4.2 Global features — `[1, F_global]`

Everything that is not per-territory, reusing the existing flat encoder's semantics verbatim: per-nation
treasury/power/rondel position, the full bond ownership block, investor-card holder, the interest-payment
preview, turn count, and the "can afford" booleans. **Copy the normalisation constants from `GetStateVector`
rather than re-deriving them** — a silently different divisor is the kind of bug that costs a training run.

---

## 5. The model

```python
class GraphEncoder(nn.Module):
    def __init__(self, a_hat, f_node, f_global, hidden=128, out=512, layers=3):
        super().__init__()
        self.register_buffer("A_hat", a_hat)              # [62, 62] constant
        self.inp = nn.Linear(f_node, hidden)
        self.gcn = nn.ModuleList(nn.Linear(hidden, hidden) for _ in range(layers))
        self.glob = nn.Sequential(nn.Linear(f_global, 256), nn.ReLU())
        self.head = nn.Sequential(nn.Linear(hidden * 2 + 256, out), nn.ReLU())

    def forward(self, nodes, globals_):
        h = torch.relu(self.inp(nodes))                   # [B, 62, hidden]
        for layer in self.gcn:
            h = torch.relu(layer(torch.matmul(self.A_hat, h)) + h)   # residual
        pooled = torch.cat([h.mean(dim=1), h.max(dim=1).values], dim=-1)
        return self.head(torch.cat([pooled, self.glob(globals_)], dim=-1))
```

Notes that matter:

- **Three layers.** Mean degree 5.13 and 3 hops reaches most of the map; more layers over-smooth (every node
  converging to the same embedding), which on a graph this small is a real risk, not a theoretical one.
- **Residual connections** are what make 3 layers safe. Do not drop them.
- **Mean *and* max pooling.** Mean alone washes out the single threatened factory that decides the turn.
- Plug into SB3 as a `BaseFeaturesExtractor` with `features_dim=out`, keeping `net_arch` for the pi/vf heads.
  The observation space becomes a `spaces.Dict({"nodes": Box(62, 39), "globals": Box(F_global,)})` and the
  policy becomes `"MultiInputPolicy"`.
- **Attention variant (optional, later):** replace the GCN stack with 2 layers of `nn.MultiheadAttention`
  over the 62 node embeddings, adjacency supplied as an attention mask. Exports to ONNX as MatMul/Softmax.
  Strictly more expressive, meaningfully slower, and it subsumes the GCN — try it only if the GCN helps.

---

## 6. ONNX export

`python_rl/export_onnx.py` currently wraps `extract_features -> mlp_extractor -> action_net` and exports a
single `[1, obs_dim]` input named `"input"`, opset 14, with a dynamic batch axis. Add a parallel path
(**do not modify the flat one** — RL through RL-4 must stay re-exportable):

```python
torch.onnx.export(
    onnx_policy,
    (dummy_nodes, dummy_globals),          # [1, 62, 39], [1, F_global]
    f"{args.bot_type}.onnx",
    opset_version=14,
    input_names=["nodes", "globals"],
    output_names=["output"],
    dynamic_axes={"nodes": {0: "batch_size"},
                  "globals": {0: "batch_size"},
                  "output": {0: "batch_size"}},
)
```

**Only the batch axis is dynamic.** Leave node count and feature width static — that is what lets the C#
side identify the model by shape (§7), and there is no case where they vary.

After export, assert `A_hat` was baked in as an initializer and did not become a graph input:

```python
assert {i.name for i in onnx.load(path).graph.input} == {"nodes", "globals"}
```

If `A_hat` appears there, `register_buffer` was missed and C# would have to supply a constant matrix on
every inference call.

---

## 7. C# inference and the compatibility guard

This is the second place that can fail silently.

`RLBotStrategy` currently reads `_onnxSession.InputMetadata.Values.First().Dimensions` and compares
`dims[1]` — a **rank-2 width check** (`RLBotStrategy.cs:189`), with the same pattern for the output
(`:160`). Its comment is explicit about the stakes: a mismatched-width tensor *"doesn't throw cleanly in all
cases, it can leave the caller's bot turn stuck mid-task"*.

**A graph model has rank-3 `nodes` input where `dims[1]` is the node count, not the feature width.** Fed to
the existing check, `62 != 3172` reads as an incompatible flat model — which is the safe direction, but only
by luck. The reverse (a flat model reaching graph code) must be impossible by construction.

So: **dispatch on the input signature, not on a width comparison.**

```csharp
// In NeuralBotStrategyBase, evaluated once per session and cached alongside it.
protected static bool LooksLikeGraphModel(InferenceSession s) =>
    s.InputMetadata.Count == 2
    && s.InputMetadata.ContainsKey("nodes")
    && s.InputMetadata.ContainsKey("globals");
```

`GnnBotStrategy.IsCompatibleWith` requires `LooksLikeGraphModel` **and** `nodes` dims `[_, 62, F_node]`;
`RLBotStrategy.IsCompatibleWith` requires a single input and keeps the existing `dims[1] == StateSize`
check. On mismatch, log an error and disable inference for that bot — the current behaviour when a model is
missing. **Never fall back to the other encoder**: feeding a graph model flat features produces confident
nonsense rather than an error, which is the single worst outcome available here.

Building the tensors is straightforward, mirroring `RLBotStrategy.cs:875`:

```csharp
var nodes   = new DenseTensor<float>(nodeBuffer,   new[] { 1, NodeCount, NodeFeatureWidth });
var globals = new DenseTensor<float>(globalBuffer, new[] { 1, GlobalFeatureWidth });
return new[] { NamedOnnxValue.CreateFromTensor("nodes",   nodes),
               NamedOnnxValue.CreateFromTensor("globals", globals) };
```

Model files need no new plumbing: `Imperial2030.Server.csproj:34,37` already globs `*.onnx` and
`*.onnx.data`, and path resolution is `$"{botType}.onnx"` under `AppContext.BaseDirectory`
(`RLBotStrategy.cs:101`). Dropping `RL-G.onnx` into `Server/` is the whole deployment step.

**Performance is a non-issue.** 62 nodes at hidden 128 is a handful of small matmuls per turn, well under
the existing `1024x512` MLP. Keep `IntraOpNumThreads = 1` — the reasoning in the session-cache comment
(outer per-session concurrency during training is the real parallelism) is unchanged.

---

## 8. Training server and Python

### `Server/Services/TcpTrainingServer.cs`

`GetStateVector` stays exactly as it is — RL-4 and earlier still train through it. Add
`GetGraphStateVector(game, rlPlayerId)` returning `(float[] nodes, float[] globals)`.

Selection must be **derived, not configured**. `HandleResetAsync` already knows `req.BotType`; reuse the
same `GraphBotTypes` set from §3.3 so a bot trains through the encoder it will infer through. A separate
`encoding` field on the wire would let the two drift, which is precisely the failure
`ResetResponse.StateSize` was introduced to prevent.

Extend `ResetResponse` — additively, per rule #17, nullable so older clients ignore them:

```csharp
[JsonPropertyName("nodeCount")]        public int? NodeCount { get; set; }
[JsonPropertyName("nodeFeatureWidth")] public int? NodeFeatureWidth { get; set; }
[JsonPropertyName("globalSize")]       public int? GlobalSize { get; set; }
```

`StateSize` / `ActionSize` keep their current meaning. `ActionSize` is unchanged at 205 either way.

### `python_rl/imperial_env.py`

Add `GraphImperialEnv` (or branch inside `reset`/`step` on what the reset response advertises) exposing
`spaces.Dict({"nodes": ..., "globals": ...})`. Extend `_check_shapes` to validate all three new widths — it
exists because a duplicated constant on the far side of the socket is exactly how this breaks.

The `shapingScale` / `factoryPenaltyScale` curriculum added for RL-4 is encoder-independent and needs no
change.

### `python_rl/train.py`

`"MlpPolicy"` -> `"MultiInputPolicy"` with `policy_kwargs=dict(features_extractor_class=GraphEncoder, ...)`,
selected by `--bot-type`. Everything else — `RunRelativeSchedule`, `EntCoefScheduleCallback`,
`CurriculumCallback`, `gamma=0.995` (measured; see `rl4_training_analysis.md` §3a), the LR schedule — is
unchanged and applies identically.

---

## 9. Implementation order

Each step ends with a green `dotnet test`.

1. **Extract `NeuralBotStrategyBase`.** Pure refactor, zero behaviour change. Move `TrainingActionOverride`
   and the action constants up; repoint the five `BotService` sites. Full suite green **before** anything
   else. Own commit.
2. **`Shared/Constants/MapGraph.cs`** — `NodeCount` and the dense normalised adjacency, with node order taken from `RLBotStrategy.AllManeuverTerritories`,
   and the dense normalised adjacency. Generated from `MapConnectivity` at static-init, not hand-typed.
3. **`GetGraphStateVector`** in the training server + `ResetResponse` fields. Testable with no model present.
4. **`GnnBotStrategy`** with `BuildInputs` / `IsCompatibleWith`. With no `RL-G.onnx` on disk it logs and
   disables inference — safe to merge before any model exists.
5. **Python encoder + env + export.** Train a deliberately tiny model first and export it purely to prove
   the ONNX round-trip and the C# input signature, before spending GPU time.
6. **Train RL-G**, opponents including RL-4.
7. **Evaluate** with `RLPerNationBehaviourTests` (the `built/max` column and the per-nation-stint line), plus
   head-to-head win rate against RL-4.

---

## 10. Tests to write

- `MapGraph` census: 62 nodes, 159 undirected edges, symmetric, and every node present in
  `MapConnectivity.Adjacency`.
- **Ordering identity:** `MapGraph`'s node order is exactly `RLBotStrategy.AllManeuverTerritories`, and
  its contents equal `MapConnectivity.Adjacency.Keys` as a set. The first keeps node row *k* and
  destination action `127 + k` the same territory; the second catches a territory added to the map but
  not to the hand-written array, which would otherwise leave a hole in the graph rather than fail.
- Adjacency normalisation: rows of `A_hat` sum to ~1 under the chosen scheme, and **no node has degree
  zero** — assert that explicitly, so a future map edit introducing an isolated node fails loudly here
  rather than producing NaN logits at inference time.
- `GetGraphStateVector` emits exactly `62 * F_node` and `F_global` floats, and every value is finite and
  within the normalised range.
- Egocentric rotation: the same board encoded from two different acting nations produces node blocks that
  are the appropriate permutation of one another.
- **Guard test:** a flat-input session is rejected by `GnnBotStrategy.IsCompatibleWith`, and a two-input
  session is rejected by `RLBotStrategy.IsCompatibleWith`. This is the silent-failure mode from §7 and is
  worth a test even though it looks trivial.
- Extend `TrainingRewardCurriculumTests`-style source scanning if any `strategy is RLBotStrategy` check
  survives step 1 — a grep-based assertion that the concrete type is not tested outside the base class.

---

## 11. Explicitly out of scope

- **Changing the action space.** 205 actions, unchanged. This is an encoder change only.
- **Retiring the flat encoder.** RL through RL-4 keep training and inferring through `GetStateVector`
  forever; rule #17 is not suspended by adding a second encoder alongside it.
- **PyTorch Geometric** — see §2.
- **Dynamic graphs.** The map is static; nothing here should be built to handle edges changing.
- **Rewriting `GameMap.razor`.** The client's SVG is unrelated to the model's graph.

---

## 12. Risks

| risk | mitigation |
|---|---|
| Silent training/inference divergence in the encoder | `ResetResponse` advertises all widths; `_check_shapes` validates. Same mechanism that already guards `StateSize`. |
| A new `strategy is RLBotStrategy` check added later | Step 1 removes them all; §10 suggests a source-scan test to keep them gone. |
| Over-smoothing on a 62-node graph | 3 layers + residuals; if node embeddings collapse, drop to 2 layers before touching anything else. |
| GNN underperforms RL-4 | Expected and acceptable on the first attempt. Keep RL-4 as the shipped bot; `GraphBotTypes` means both coexist with no cutover. |
| `A_hat` exported as an input instead of a constant | Asserted at export time (§6). |
