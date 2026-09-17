# Extract the game engine from the controllers

> Replaces the previous plan in this file ("Correct PPO schedule persistence"), which is implemented:
> `python_rl/training_schedules.py`, `python_rl/test_training_schedules.py`, and the per-bot
> `*_schedule_state.json` files all exist.

Status: **ALL PHASES DONE (Phase 7 on 2026-09-17). The three grep invariants are zero.**

| Phase | Status | Result |
|---|---|---|
| 0 | done | Baseline 423 → suite now 457 (11 new `RondelEngineTests`); divergence survey below; `EngineResult` added |
| 1 | done | `HandleInvestorPhase`, `UpdateNationController` → `Server/Engine/InvestorEngine.cs`; 22 call sites retargeted; `GamesController.` is no longer called from `Server/Services` for logic |
| 2 | done | `Server/Engine/RondelEngine.cs`; `GamesController.MoveNation` 197 → 50 lines; `BotService.ExecuteBotTurn`'s copy (97 lines) deleted; review §M3 closed |
| 3 | done | `TurnEngine`, `FactoryEngine`, `TaxationEngine`, `ProductionEngine`, `ImportEngine`. Five endpoints 405 → 189 lines; `BotService` copies of factory/production/import deleted; `TcpTrainingServer` copies of factory/import deleted and its mask legality now comes from the engine (action-space ORDER untouched). Rule #17 check: 13k–24k ONNX actions per model, 0 invalid; win rates RL-4 82%, RL-3 54%, RL-2 62%. **A live training run then crashed** on the engine's guards — three pre-existing training/bot ordering bugs (#13–#15) that the deleted copies had been hiding; fixed test-first, then 50k random-action smoke steps clean. Suite 476 |
| 4 | done | `InvestorEngine.Buy` / `Pass` / `RespondToSwissBank`. `PerformInvestment` 131 → 45, `SwissBankResponse` 105 → 28, `BotInvestorAction` 106 → 34, `HandleBotSwissBankResponse` 82 → 30. The Swiss Bank paths' own copy of the rondel move is gone — both outcomes are `RondelEngine.MoveNation`. `GamesController` 1,606 → 1,429 lines. Suite 487; smoke 30k steps clean, then a live crash (row 18) fixed and 60k more steps clean |
| 5 | done | `ManeuverEngine` (Stay, MoveFleet, MoveArmy, ToggleHostility, ResolveStationaryBattle, StationaryBattle, DestroyFactory, FactoryDestructionCandidates, RespondToBattle, EndPhase, TryAutoAdvanceManeuver, UpdateTerritoryControl). `ManeuverController` 1,098 → 250 lines; `BotManeuver` 386 → 116 plus two decision helpers; the training server's inline move (~100 lines) and its own fleet-legality mask are gone; `RondelEngine.AutoSkipEmptyManeuverPhases` folded into `TryAutoAdvanceManeuver` (row 1 closed). Two rules bugs in the endpoints fixed against the PDF (rows 19, 20). Suite 501; smoke 60k steps clean |
| 6 | done | `Server/Engine/GameLifecycle.cs` — `Start(context, game, forcedDistribution, startedBy, roster?)`: board, deal, governments, investor card, cash, first turn and the `StartGame` entry. `GamesController.StartGame` 75 → 56 lines; `GameSetupHelper` 394 → 130 lines (`InitializeGameAsync` is now a load-call-save wrapper) the replay/import/test call sites keep using; `TcpTrainingServer.HandleResetAsync`'s copy (180 lines) deleted; `ImportGame` no longer logs its own `StartGame` (the roster it remaps is passed through `ReconstructRosterAndSetupAsync`). `GameLifecycleTests` (6). Suite 517; smoke 50k steps clean (988 training games set up by the engine, zero errors) |
| 7 | done | `GameReplayService.ReplayActionsAsync(context, gameId, actions, onActionReplayed?)` — no controllers, no `suppressBroadcasts`; every replayed action is an engine call (`RondelEngine.MoveNation`, `ManeuverEngine.MoveArmy/MoveFleet/RespondToBattle/DestroyFactory/UpdateTerritoryControl`, `TaxationEngine`, `FactoryEngine`, `InvestorEngine.Buy/Pass`, `TurnEngine.EndTurn`) followed by the same `TryAutoAdvanceManeuver` the endpoint runs; refusals are `EngineResult.Error`, and the replay's caller-identity set-up is gone (only Investment's `ActingPlayerId` is still pinned). `ReplaySessionManager` constructs no controllers and needs no scope factory; its snapshot uses the new `GameDetailDtoBuilder` (the projection moved out of `GamesController.GetGame`). `SuppressBroadcasts` and its 30 checks deleted. Row 31 fixed (and the same save-then-log loss in the Battle handler), test-first. Suite 519 |

**Amendment (Phase 1):** `InvestmentActionDto` stays on the controller. It is the HTTP request shape
(`ActionType = "Pass" | "Buy"`), not engine input; the engine's investment methods (Phase 4) take typed
arguments. Its only non-HTTP reference is `GameReplayService` calling the endpoint, which goes away in
Phase 7. So the first grep invariant below still shows those two lines until then.

## The problem, measured

The game engine lives inside the two HTTP controllers, and the three non-HTTP consumers of that engine
each work around it differently. Numbers are from the working tree on 2026-09-15.

**Where the rules are implemented today**

| Operation | `GamesController` / `ManeuverController` | `BotService` | `TcpTrainingServer` |
|---|---|---|---|
| Rondel move | `MoveNation` (197 lines, L1349) | re-implemented in `ExecuteBotTurn` — `docs/code_review.md` §M3, still open | via `BotService` |
| Investor phase | `HandleInvestorPhase` — **`public static` on the controller** (195 lines, L1066) | calls the static, 3 sites | via `BotService` |
| Nation control | `UpdateNationController` — **`public static` on the controller** (88 lines, L1261) | calls the static | via `BotService` |
| Build factory | `BuildFactory` (74 lines, L1789) | own copy — `ns.Treasury -= FactoryCost; ts.HasFactory = true` (L645) | **third copy** — `ns.Treasury -= 5; ts.HasFactory = true` (L729) |
| Import | `ExecuteImport` (95 lines, L2000) | own copy (L1385) | **third copy** (L786) |
| Buy bond | `PerformInvestment` (139 lines, L1650) | own copy — `bondToBuy.HolderId = actor.Id; actor.Cash -= …` (L1408) | via `BotService` |
| Game setup | `StartGame` (77 lines, L979) → `GameSetupHelper` | — | **own copy of `GameSetupHelper`** (L340–460, same `bond9M`/`bond2M` logic) |
| Maneuver / battle | `MoveArmy` (264), `MoveFleet` (247), `Battle`, `BattleResponse`, `ToggleHostility`, `DestroyFactory`, `NextPhase` | via `ManeuverHelper` + own decisions | own inline move at L900–960 |
| Investment DTO | `GamesController.InvestmentActionDto` — a DTO **nested in the controller class** | — | referenced by `GameReplayService` L1010 |

**The workarounds this forces**

- `ReplaySessionManager` L381–382 and `GamesController.ImportGame` L646–647 do
  `new GamesController(...)` / `new ManeuverController(...)` and call `[HttpPost]` endpoint methods
  as engine calls.
- `SuppressBroadcasts` — a flag on both controllers, checked at **51 sites** — exists only so those
  `new`-ed controllers don't push SignalR events.
- `GameReplayService.ReplayGame(...)` takes `GamesController` and `ManeuverController` **as parameters**
  (L274).
- `Tests/` has 32 `new GamesController(...)` / `new ManeuverController(...)` constructions across 9 files;
  20 of them in `ReplayGameTests.cs` exist purely because replay needs controllers.

**Why this is a correctness problem, not a taste problem**

Three copies drift. Two of this session's bugs were exactly that:

- **`TrainingFactorySlotTrapTests`** (fixed 2026-09-06, commit `bdf5d03`): the Factory decision gate in
  `TcpTrainingServer`'s copy was derived from persistent `RondelPosition`; `BotService`'s copy was
  correctly derived from the slot landed on this turn. Only the training copy was wrong, so only the RL
  RL agent's nations froze — four of them, for ~7,500 log entries.
- **`docs/code_review.md` §M3**: `BotService.ExecuteBotTurn`'s copy of `MoveNation` had already drifted
  once on phase initialisation ("harmlessly", that time).

Every future rule fix has to be applied in up to three places, and nothing checks that it was.

## Goal

**One implementation of each game operation**, callable identically from HTTP, bots, training and replay.
Controllers become: authorise → load → call engine → save → broadcast → trigger bot. The three copies
are deleted, not kept in sync.

This is a **pure refactor**. `Imperial-2030-Rules.pdf` behaviour does not change, the HTTP routes and
DTOs do not change, the RL state/action layout does not change (`.agents/AGENTS.md` rule #17), the
database schema does not change. The 423-test suite is the guard, and passes at every phase boundary.

### Non-goals

- No MediatR / CQRS / mediator library — see the discussion that led here; it would wrap the same
  bodies in request/handler pairs and add a dispatch layer to the training loop.
- No DI-injected engine object. The codebase's existing convention for engine logic is **static helpers
  taking `(ApplicationDbContext? context, Game game, …)`** — `TaxationHelper`, `ManeuverHelper`,
  `InvestorHelper`, `GameSetupHelper`, `GameLogger`, and the two statics already on the controller
  (`HandleInvestorPhase`, `UpdateNationController`) all have that shape. The plan extends the convention
  rather than introducing a second one. Training passes `context: null` today and keeps doing so.
- No change to what any endpoint returns, no new endpoints, no removal of endpoints.

## Target shape

```
Server/Engine/                       new folder; static classes, same convention as Server/Helpers
  EngineResult.cs                    Ok | Fail(message) — replaces the 137 inline BadRequest("...") strings
  RondelEngine.cs                    MoveNation body                         closes review §M3
  InvestorEngine.cs                  HandleInvestorPhase, UpdateNationController (moved, not rewritten),
                                     PerformInvestment body, SwissBankResponse body (DTO stays on the controller - see amendment)
  FactoryEngine.cs                   BuildFactory body, DestroyFactory body
  ProductionEngine.cs                ExecuteProduction body
  TaxationEngine.cs                  ExecuteTaxation body (TaxationHelper already owns the arithmetic)
  ImportEngine.cs                    ExecuteImport body
  ManeuverEngine.cs                  MoveArmy, MoveFleet, Battle, BattleResponse, ToggleHostility,
                                     NextPhase, UpdateTerritoryControl, ResolveBattles, TryAutoAdvanceManeuver
  TurnEngine.cs                      EndTurn body
  GameLifecycle.cs                   StartGame body; absorbs GameSetupHelper AND TcpTrainingServer's copy
```

Signature for every engine method:

```csharp
public static EngineResult Do(ApplicationDbContext? context, Game game, Player actor, <operation args>)
```

- `game` is already loaded by the caller (the `.Include()` chains stay in the controller — they are a
  persistence concern, and training/replay games are in memory).
- The engine **mutates `game` and logs** via `GameLogger` (which already takes `ApplicationDbContext?`).
- The engine **does not** `SaveChanges`, broadcast, build toasts, or trigger bots. Those stay with the
  caller. Where a toast needs facts from inside the operation (the investment toast at
  `GamesController` L1743), the `EngineResult` carries them.
- Authorisation (`User.FindFirstValue`, `Forbid()`) stays in the controller. Verified: all 42 uses of
  `User.`/`Request.` in the two controllers are at the top of endpoint bodies, before any rule logic.

What a controller looks like afterwards, using `EndTurn` (today 43 lines, L1863):

```csharp
[HttpPost("{gameId}/end-turn")]
public async Task<IActionResult> EndTurn(Guid gameId)
{
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Unauthorized();

    var game = await LoadGameForTurn(gameId);            // the existing Include chain
    if (game == null) return NotFound();
    var actor = ActingPlayerOrNull(game, userId);         // existing controller-check block
    if (actor == null) return Forbid();

    var result = TurnEngine.EndTurn(_context, game, actor);
    if (!result.Ok) return BadRequest(result.Error);

    await _context.SaveChangesAsync();
    await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
    _botService.TriggerBotTurn(gameId);
    return Ok();
}
```

## The one rule that governs every move

When a `BotService` or `TcpTrainingServer` copy is replaced by the engine call, **diff the copy against
the controller body first**. Where they differ:

1. **Stop.** Do not pick one silently.
2. Write a test that shows the difference.
3. Decide which is right **against `Imperial-2030-Rules.pdf`** — it may be either. Today's factory trap
   was in the training copy; review §M7's last-factory bypass was in a controller.
4. Fix, record it in the divergence table below, then continue.

This is `.agents/AGENTS.md` rules #21/#22 applied to a refactor: the refactor may not change behaviour,
so a behaviour difference between copies is a finding, not a merge conflict.

## Phases

Each phase ends with `dotnet test` fully green. Each is independently shippable — stopping after any
phase leaves the codebase strictly better than before it.

### Phase 0 — Baseline and divergence survey (no code moves) ✅ done

- Record the suite baseline (423 passing as of 2026-09-15).
- For each row of the table above with a `BotService` or `TcpTrainingServer` copy, produce a written
  diff of the copy against the controller body and fill in the divergence table's "known before start"
  column. This is the survey the code review did not do.
- Add `EngineResult` (`readonly record struct EngineResult(bool Ok, string? Error)` plus a small payload
  slot for the investment-toast facts) and its tests.

### Phase 1 — Move what is already static ✅ done

`HandleInvestorPhase`, `UpdateNationController`, `InvestmentActionDto` → `InvestorEngine`. Pure move;
their signatures already match the target. Update the 4 `BotService` call sites and the 2
`GameReplayService` references. **Zero behaviour risk**; the only purpose is to make
`GamesController.` unreferenced from `Server/Services/`.

### Phase 2 — Rondel move (closes review §M3) ✅ done

Extract `MoveNation`'s body to `RondelEngine.MoveNation`. Replace `BotService.ExecuteBotTurn`'s
re-implementation with the call. **This is the first phase where the divergence rule fires** — §M3
already documented one drift. The factory-decision record (`buildPending` in `BotService` L433, and the
`FactoryDecisionOwedBy` flag in `TcpTrainingServer`) must survive: the engine reports "landed on Factory
with a decision owed", the callers arm their own state from that.

### Phase 3 — Single-step operations ✅ done

In this order, smallest and best-tested first:

1. `EndTurn` → `TurnEngine` (43 lines; `TurnRotationTests` guards `AdvanceTurn`)
2. `BuildFactory` → `FactoryEngine`; delete `BotService` L645 and `TcpTrainingServer` L729 copies
   (`TrainingFactorySlotTrapTests`, `BotFactorySkipLoggingTests`, `BotLastFactoryBlockadeTests` guard)
3. `ExecuteTaxation` → `TaxationEngine` (arithmetic already in `TaxationHelper`/`TaxationRules`)
4. `ExecuteProduction` → `ProductionEngine`
5. `ExecuteImport` → `ImportEngine`; delete `BotService` L1385 and `TcpTrainingServer` L786 copies.
   Shape chosen: `PlaceOne` (one unit, validated, paid) is the primitive; `Import(units)` validates the
   whole request against the rules and the cap as a group, then places through it; `CompleteImport`
   marks the turn's import done and logs. Endpoint and bot call `Import`; training calls `PlaceOne` per
   agent step and `CompleteImport` when the sequence ends. `GameConstants.MaxImportUnits` added (was a
   bare 3 in three places).

For 2 and 5, `TcpTrainingServer`'s step handler changes. **Verify rule #17 after this phase**: run
`RLVersionComparisonTests` / `TestRLBotWinRate` and confirm the training-server action counters are
non-zero for every deployed `.onnx` — the state/action layout is untouched, but the code that applies
actions is not, and that is exactly where a silent no-op would hide.

### Phase 4 — Investor and Swiss Bank ✅ done

`PerformInvestment` and `SwissBankResponse` bodies → `InvestorEngine`. Replace `BotService`'s bond
purchase (L1408) and `HandleBotSwissBankResponse` internals with the calls. The investment toast's facts
come back on the `EngineResult`; the controller builds and sends the toast as today.

### Phase 5 — Maneuver ✅ done

`MoveArmy`, `MoveFleet`, `Battle`, `BattleResponse`, `ToggleHostility`, `DestroyFactory`, `NextPhase` and
the private helpers → `ManeuverEngine`. Largest phase (~1,000 lines). Constraints:

- The replay-only `BattleTargetNation` / `BattleTargetUnitType` passthrough (`CLAUDE.md` § Replay) stays
  an engine parameter — this is the mechanism rule #22 protects.
- `ManeuverHelper.IsProtectedLastFactoryProvince` is called from three endpoints today (review §M7);
  after the move it is called from the engine and the count goes to one.
- `TcpTrainingServer`'s inline move at L900–960 is replaced by the engine call; its penalties
  (`IsRedundantStackMove`, `DeclinedHomeReliefPenaltyFor`, `IsPointlessReversal`) stay in the training
  server — they are reward shaping, not rules — and read the engine's result.
- `TestImportFromExportedJson` and the whole of `ReplayGameTests` guard this phase.

### Phase 6 — Lifecycle

`StartGame` body → `GameLifecycle.Start`, absorbing `GameSetupHelper` **and** `TcpTrainingServer`'s
setup copy (L340–460). ✅ done. `GamesController.ImportGame`'s `new GamesController(...)` does NOT go
away here after all: it exists to feed `GameReplayService.ReplayActionsAsync`, which still drives
endpoints - it goes with Phase 7. `GameSetupHelper.ReconstructRosterAndSetupAsync` (creates placeholder
identity users for an import) stays a helper: identity is not a rule.

### Phase 7 — Replay and cleanup ✅ done

- `GameReplayService.ReplayActionsAsync` takes no controllers; `ReplaySessionManager` constructs none.
- Delete `SuppressBroadcasts` and all its checks (30 by the time of deletion, 51 when this was written -
  the phases in between had already removed the ones in code that moved to the engines).
- `ReplayGameTests`' replay-side controller constructions are gone (the tests hand the service the
  context and the actions). The constructions that play the ORIGINAL game through the endpoints stay:
  a test that constructs a controller to exercise an endpoint is a legitimate HTTP-layer test.
- `GameSetupHelper` is deleted (absorbed in Phase 6). **Not done as written**: what is left of it is
  `ReconstructRosterAndSetupAsync` (creates placeholder identity users for an import - not a rule) and the
  15-line `InitializeGameAsync` load-call-save wrapper the replay/import/test call sites use. Both call
  `GameLifecycle.Start`; nothing else is duplicated. Kept.

## Audit of the rows above (2026-09-16, requested after Phase 5)

Every row that rests on a rulebook reading was re-checked against `pdftotext` output with page
numbers, not memory. Results:

- **Rows 10 and 13 cited a rule that does not exist** ("the investor phase resolves as part of the
  movement, before the destination's action"). p.11 says the reverse for a pass-over. The codebase
  as a whole - endpoint, bot, engine - had always run the Investor turn first; my Phase 3 change made
  training consistent with that and justified it with the fabricated sentence. The wording is corrected
  in the code, the tests and the rows; the real fix is **row 28**, done 2026-09-17.
- **Row 21 was the wrong call** (a fight now closes the negotiation; corrected on the user's objection).
- **Row 29** above: a replay-ordering regression introduced in Phase 5 and fixed.
- **Row 30** above: the real cause of the intermittent replay failures - a missing `Include` on `MoveNation`
  that Phase 5 turned into duplicate flag rows. Found by re-importing the test's own captured repro
  (`Tests/TestArtifacts/ImportReplayDivergence_repro.json`) deterministically instead of re-rolling games.
- Three page numbers were wrong in engine docs (Factory p.11 → p.7, Import p.11 → p.8, game end
  p.12 → p.6) and two paraphrases were in quotation marks (Import, Production); all replaced with the
  rulebook's own sentences.
- Verified as quoted: rows 4, 16, 19, 20; the Swiss Bank condition ("sufficient money to pay out all
  interest", p.12); bond upgrade "pay only the difference" (p.11); `GetConvoyFleets` is
  `GetAllReachableArmyDestinations(...).ConvoyFleets`, so the bot's convoy fleets did not change.

## Divergence table

Filled in as the work proceeds. Every row is a place where two copies of the same rule disagreed.

| # | Operation | Copy A | Copy B | Rulebook says | Finding | Test | Phase |
|---|---|---|---|---|---|---|---|
| 0 | Factory decision gate | `BotService` L433: slot landed on this turn | `TcpTrainingServer` L693: persistent `RondelPosition` | p.7, rondel positions persist | **B wrong** — trapped the RL agent's nations | `TrainingFactorySlotTrapTests` | fixed 2026-09-06, before this plan |
| 1 | Post-move empty maneuver phases | `MoveNation`: skips empty Fleets/Armies at once, logs `AutoSkipPhase` | `ExecuteBotTurn`: no skip; `BotManeuver` walks both phases and logs `AutoEndPhase` for each | silent (logging) | **Neither wrong.** Same end state; log and intermediate phase differ. Resolved in Phase 5: one implementation, `ManeuverEngine.TryAutoAdvanceManeuver` (flags + phase + `AutoEndPhase` log), called after each HTTP move and at a Maneuver landing; the bot and training drive their phases explicitly through the same `UpdateTerritoryControl`. `AutoSkipPhase` is no longer produced for Fleets/Armies (replay ignored it) | `RondelEngineTests`, `ManeuverEngineTests` | 2 → 5 ✅ |
| 2 | Rondel move validation | `MoveNation`: 9 checks (status, investor, turn, slot, government, moved, same slot, distance, cash) | `ExecuteBotTurn`: none at mutation time; `ChooseRondelSlot` pre-filters distance and cash | p.6 | **Not a conflict** — the bot's choices always satisfy the checks. Engine validates for both; the bot now throws on a rejection instead of applying it | `RondelEngineTests` | 2 |
| 3 | Factory site: missing `TerritoryState` | `BuildFactory`: `BadRequest("Territory state not initialized.")` | bot and training: create it | silent | Both setups create every territory's state, so unreachable; engine will create (permissive) | — | 3 |
| 4 | Factory site filter | `BuildFactory` / bot: `IsHomeCity` (home **and** `CityType != None`) | training `GetFactoryBuildOptionsFor`: `t.Nation == ns.Nation` (every home province) | p.7: factories are built in cities | **Training filter wrong**, latent — every home province on this map is a city (verified: 4/4 for all six nations) | — | 3 |
| 5 | Import validation | `ExecuteImport`: max 3, ≥1, treasury, home province, no hostile army, harbour for fleets, unit caps — at mutation | bot: in the strategy's `ChooseImports`; training: in `GetImportOptions` mask | p.7 | **Not a conflict** — same rules, enforced by the caller's decision code rather than at mutation; `BotService.AddUnit` applies whatever the strategy returns. Engine validates at mutation for all three | — | 3 |
| 6 | Investment | `PerformInvestment`: cash check, trade-in, controller update, queue advance | `BotInvestorAction`: no trade-in, no cash check at mutation, same queue advance | p.11–12 | **Bot is a subset**, not a conflict; `ChooseBondToBuy` pre-filters affordability | — | 4 |
| 7 | Game setup | `GameSetupHelper` | `TcpTrainingServer` L260–470 | p.4 | **Same algorithm**: 125/176 normalised lines identical, the rest are renames and the training roster; starting cash 13 in both for six players | — | 6 |
| 8 | End of turn | endpoint: 3 guards + `EndTurn` log; bot: maneuver guard + log | training: bare `game.AdvanceTurn()`, no guard, no log | p.7 | **Training wrong (log)** — exported training games show a Factory/Import/Maneuver turn followed by the next nation's Move with no `EndTurn`. Engine guards and logs for all; training throws if its own "turn is finished" condition disagrees | `RondelActionEngineTests` | 3 ✅ |
| 9 | Factory build log | endpoint and bot: `LogFactoryBuild` | training: none | silent | **Training wrong (log)** — the export has 0 `Factory` entries for the RL agent. Engine logs for all | `RondelActionEngineTests` | 3 ✅ |
| 10 | Factory decision vs open investor phase | endpoint: refuses while `IsInvestorTurn` | training: asked for the factory decision first when the move to Factory crossed Investor (Maneuver 1 → Factory, six spaces) | **p.11 actually says the TRAINING order was right**: on a pass-over "the action determined by the space landed on is completed first". The plan originally cited the opposite here - a fabricated reading, caught in the Phase 5 audit | Training was made to wait for the open Investor turn like the endpoint and the bot do, so all three paths agree; the order itself was then fixed in row 28 | `TrainingFactorySlotTrapTests.TheDecisionWaitsForAnOpenInvestorPhase` | 3 ✅, order fixed in row 28 |
| 11 | Empty production | endpoint: `HasProducedThisTurn` left false, "No units produced" in the HTTP body only | bot: flag set, reason logged (`ProductionNoFactories` / `Blockaded` / `AtUnitCap`) | silent | **Neither a rules conflict**; bot's shape taken for all — the action was taken, so the once-per-turn guard applies, and the reason belongs in the game log. HTTP re-POST after an empty production now gets "Already produced" instead of a second empty run | `RondelActionEngineTests` | 3 ✅ |
| 12 | Import log | endpoint and bot: `LogImport` / `LogImportedNothing`; bot also `LogImportNotAfforded` | training: none | silent | **Training wrong (log)**. Engine `CompleteImport` logs for all; training also logs not-afforded the way the bots do | `RondelActionEngineTests` | 3 ✅ |
| 13 | Slot action vs open investor phase — Import and maneuver | endpoint: every slot action refuses while `IsInvestorTurn` | training: mask and dispatch checked `PendingImportRemaining`, factory destruction and the maneuver branches BEFORE `IsInvestorTurn`; action 63 (pass investment / end maneuver phase) went to the maneuver branch; the same-step end-of-turn ignored the phase | as #10 - the rulebook order is the reverse | **Same as #10** — found by the crash of a live run ("Waiting for Investor Phase" from the engine, twice). The open Investor turn now takes precedence everywhere in the step handler, matching the endpoint and the bot: `IsImportSequencePending`, `MayRondelActionEndTheTurn`, `!IsInvestorTurn` on the destruction/maneuver branches, recording only on the rondel-move step. Once the phase clears, `ExecuteBotTurn` finishes the turn as it already does for Production. With row 28 done, a pass-over no longer opens an Investor turn before the action; these guards remain for one opened any other way | `TrainingFactorySlotTrapTests.TheImportSequenceWaitsForAnOpenInvestorPhase` (failed before) | 3 ✅ |
| 14 | Import sequence scope | factory decision: `FactoryDecisionOwedBy` = nation, dropped when the turn moves on | import sequence: `PendingImportRemaining` only — session-wide, no nation | — | **Training wrong** — same shape as the factory trap. Found by the smoke run: a government change in the investor phase the move opened let a bot finish Europe's turn, and the still-recorded sequence was offered to the RL agent's next nation ("Not in Import phase" from the engine). `ImportSequenceNation` added, checked in the gate and dropped at the top of the step | `TrainingFactorySlotTrapTests.TheImportSequenceDoesNotOutliveTheNationThatStartedIt` (failed before) | 3 ✅ |
| 16 | Deferred rondel move after a Swiss Bank answer | `MoveNation` endpoint: move then `AutoSkipEmptyManeuverPhases` | `SwissBankResponse` and `HandleBotSwissBankResponse`: a THIRD copy of the move (cost, position, reset, log, investor), and no empty-phase skip afterwards | p.6, p.11 | **Both Swiss Bank copies agreed with each other** and disagreed with the endpoint only on the skip. Engine: both outcomes are `RondelEngine.MoveNation`, which already handles a pending forced stop; the skip stays HTTP-only as in row 1, Phase 5 owns it | `InvestorEngineTests` | 4 ✅ |
| 17 | `tookControl` for the investment toast | endpoint: old controller id ≠ new | bot: old controller NAME ≠ new | — | Toast-only; engine uses ids | — | 4 ✅ |
| 18 | A move stopped by a Swiss Bank | `TurnEngine.EndTurn` (and the endpoint before it): no guard for a pending Swiss Bank question | training: "landed on Factory/Import" was read from `RondelPosition` alone, which a stopped move leaves where it was - possibly on that slot from an earlier turn | p.12: the move has not happened until the Swiss Banks answer | **Both wrong.** Live crash: Brazil's turn was ended with its question open, and the answer then tried to complete Brazil's move on USA's turn. `EndTurn` now refuses while a question is pending; the training server's `LandedThisTurn` requires `HasMovedThisTurn` | `RondelActionEngineTests.EndTurnRefusesWhileAnythingIsStillOpen`, `TrainingFactorySlotTrapTests.AStoppedMoveIsNotALanding` (both failed before) | 4 ✅ |
| 19 | Hostile entry into a nation's last unoccupied factory province | `MoveArmy`/`MoveFleet` endpoints: allowed when the entry would cause a battle (`willFight`) | bot and training: always downgraded to peaceful | p.10: "may not be entered by hostile armies. Armies of other nations that enter this province are laid down on their sides" — no battle exception | **Endpoint wrong.** Engine refuses the hostile entry for every caller; bots and training lay the unit down before calling | `ManeuverEngineTests.TheLastUnoccupiedFactoryProvinceMayNotBeEnteredHostilelyEvenToFight` | 5 ✅ |
| 20 | A fleet arriving peacefully among foreign fleets | `MoveFleet` endpoint: they coexist, nobody asked | bot: each foreign fleet asked whether it fights (as for armies) | p.8: "he has to offer the opportunity for a battle to each other fleet present in the sea region... asked one after another" | **Endpoint wrong.** Engine opens the negotiation for fleets and armies alike | `ManeuverEngineTests.AFleetArrivingPeacefullyAmongForeignFleetsAsksThemWhetherTheyFight` | 5 ✅ |
| 21 | A defender's "fight" answer | `BattleResponse` endpoint: a fight ended the negotiation for everyone | bot: removed only the answering nation and kept asking the rest while the aggressor had any unit left there | p.8: the defenders are asked about the unit that arrived; once it has been fought it is gone and there is nothing left to answer | **Endpoint right, bot wrong.** The engine first took the bot's shape (a wrong call, caught by the user); a fight now closes the negotiation | `ManeuverEngineTests.AFightEndsTheNegotiationEvenIfTheAggressorHasOtherUnitsThere` (failed before the correction) | 5 ✅ |
| 22 | Maneuver log on the training path | endpoint and bot: every move, stay, battle and hostility change logged | training: none of them | silent | **Training wrong (log)**, as rows 8, 9, 12. Engine logs for all | smoke run | 5 ✅ |
| 23 | Player named in the `EndPhase` entry | `NextPhase` endpoint: `game.ActingPlayerId`, which is null outside an Investor turn — logged as "Unknown" | — | silent | Engine names the nation's government | — | 5 ✅ |
| 24 | `ResolveBattles` in `NextPhase` | ran only when the phase was neither Fleets nor Armies — where the endpoint then returned 400 | — | — | Dead code; not carried over | — | 5 ✅ |
| 25 | Player named in flag-placement entries | endpoint: the government of the nation whose flag it is | bot: the acting bot, for every nation's flags | silent | Engine names the flag's owner | — | 5 ✅ |
| 26 | A fleet standing up in a foreign harbor | `ToggleHostility` endpoint: refused in a last unoccupied factory province | bot fleet loop: no check (the army loop had one) | p.10 | Engine refuses for any unit; the bot asks the engine first | `ManeuverEngineTests.StandingUpInTheLastUnoccupiedFactoryProvinceIsRefused` | 5 ✅ |
| 27 | Convoy bookkeeping on a refused move | `MoveArmy` endpoint: flagged the carrying fleets `HasConvoyed` BEFORE the p.10 protection check, so a refused move spent the carriers | — | — | Engine checks first, then flags | — | 5 ✅ |
| 28 | Investor activation on a pass-over — ORDER | every path (endpoint, bot, engine): `MoveNation` activated the investor the moment the marker passed Investor, so the Investor turn ran BEFORE the nation's action, and a rival could take the government before it acted | old training: action first, then the Investor turn (but then broke in other ways - #13) | p.11: "Steps two and three are also executed when the Investor space is not landed on but is only passed over. In this second case, **the action determined by the space landed on is completed first.**" | **Fixed (2026-09-17).** `Game.InvestorTurnPending` (migrations for both providers) is set by `MoveNation` on a pass-over instead of opening the Investor turn; `TurnEngine.ActionComplete` (from `EndTurn` and from Taxation) opens it once the action is done; `InvestorEngine.AdvanceInvestorQueue` moves the rotation on (`TurnEngine.MoveOn`, logs `EndTurn`) when it ends. Landing on Investor is unchanged. Replay opens a pending Investor turn at the log's first `Investment` entry, wherever the recording placed it, so games recorded in the old order still replay; its `EndTurn` handler skips an entry for a nation whose rotation already moved on | `InvestorPassOverOrderTests` (7) | ✅ |
| 29 | Order of a bot's stand-up-and-stay log entries | pre-Phase-5 bot: `ToggleHostility` only (the stay was not logged) | Phase 5 first cut: stay entry, THEN the toggle | — (replay contract, not a rule) | **Phase 5 wrong.** Replay identifies a toggle's unit as "hostility differs and not yet moved"; with the stay first, two identical units in one territory let the replay stand up the OTHER one, and the boards then differ silently. Seen as `TestImportFromExportedJson` failing 1 in ~67 runs (baseline at Phases 0–4: 0 in 20). Order is now toggle, then a stay carrying the post-toggle hostility | `BotManeuverReplayOrderTests` (failed with the stay first) | 5 ✅ |
| 30 | `MoveNation` endpoint and the flag pass | `MoveNation` loaded the game without `TerritoryStates`; harmless while its post-landing step only skipped empty phases | Phase 5 folded that step into `TryAutoAdvanceManeuver`, which places flags - and `UpdateTerritoryControl` on an unloaded collection creates a SECOND state row for every occupied region | — | **Phase 5 wrong** (live play too, not only replay). Each duplicate row with a controller counted as a flag at taxation: the actual cause of the `TestImportFromExportedJson` failures, found through the test's own captured repro (Ukraine and Turkey each held two rows for Europe). `Include(TerritoryStates)` added | `MoveNationFlagRowsTests` (failed before) | 5 ✅ |
| 31 | Replay loses `ToggleHostility` entries | replay's handler saves the unit, THEN logs the entry; the loop clears the change tracker before the next save | — | — | **Pre-existing** (replay-only). The board is right, the re-logged log lacks the entry, so a replay of the replay loses the posture change. Not player-chosen, so `TestImportFromExportedJson` does not see it. Fixed in Phase 7: log before save. The `Battle` handler had the same save-then-log order and lost its entry the same way; fixed alongside | `ReplayGameTests.ReplayedToggleHostility_IsKeptInTheTargetsLog`, `ReplayedBattle_IsKeptInTheTargetsLog` (both failed before) | 7 ✅ |
| 32 | Set-up: training copy vs `GameSetupHelper` | helper: 2/3-player deals, first turn advanced until a governed nation, `StartedAt` set by the endpoint, roster in DB order | training: 4-6-player deal only, first turn advanced once, no `StartedAt`, roster `BotName ?? System` | p.4-5, both copies agree with it | None reachable: training games always have six players, all nations governed, every player a bot with a name. Engine keeps the helper's behaviour; training games now also get `StartedAt` | `GameLifecycleTests` | 6 ✅ |
| 15 | Bot re-entering a turn whose slot action is done | endpoint: `HasImportedThisTurn` / `HasBuiltThisTurn` / `HasProducedThisTurn` guards | `BotImport` / `BotBuildFactory` / `BotProduction`: none — `ExecuteBotTurn` re-enters with `HasMovedThisTurn` set (a government taken over mid-turn, p.12) and re-ran the slot action | p.7: one action per turn | **Bot wrong** — the inline copies imported/built/produced a SECOND time, silently. Found by the smoke run ("Already imported this turn" from the engine). Each bot slot action now returns when the flag is already set and the turn ends | `BotRondelActionReentryTests` (3, all failed before) | 3 ✅ |

## Verification

| When | What |
|---|---|
| End of every phase | `dotnet test` — full suite, all green |
| End of Phase 3 and 5 | Rule #17 check: `RLVersionComparisonTests`, `TestRLBotWinRate`, training-server action counters non-zero per model |
| Any phase that touches `TcpTrainingServer` or `BotService` | **Training smoke run** — `python_rl/smoke_env.py` against a `--training` server, ≥5 seeds × 10k random masked steps, then zero `ERROR` lines in the server log. Random actions reach turn orderings a trained policy never takes; the unit suite (476 green) missed divergences #13–#15, the smoke run found all three within 20k steps. Writes nothing to `python_rl/`. Needs `ASPNETCORE_ENVIRONMENT=Development` (or `Jwt__Key`) and the Python 3.13 install train.py uses |
| End of Phase 5, 6, 7 | `TestImportFromExportedJson`, all of `ReplayGameTests` — Phase 5: 14/14 green |
| End of Phase 7 | The three grep invariants below are all zero — ✅ 2026-09-17 (the only `new GamesController(` hits in `Server/` are two comments about test constructions) |

The invariants that define "done":

```bash
grep -rn "GamesController\.\|ManeuverController\." Server/Services Server/Helpers   # 0 hits
grep -rn "new GamesController(\|new ManeuverController(" Server                     # 0 hits
grep -rn "SuppressBroadcasts" Server Tests                                          # 0 hits
```

## Size

Roughly 2,000 lines move out of the controllers, ~400 lines of `BotService` duplicates and ~300 lines of
`TcpTrainingServer` duplicates are deleted, and `SuppressBroadcasts` removes 51 conditionals. Phases 1–3
are small and mechanical; Phase 5 is the one that needs care. No time estimate is given here because it
would be a guess; the phase boundaries are the unit of progress.

## Risks

- **Behaviour drift disguised as refactor.** Mitigated by the divergence rule and the suite. The
  divergence table is the audit trail.
- **EF change tracking.** Controllers set `_context.Entry(game).State = Modified` in places. The engine
  mutates a tracked entity and logs through `GameLogger(context?)`, exactly as `HandleInvestorPhase`
  does today; `SaveChanges` stays with the caller. `BotService` already uses its own scoped context and
  `SaveChangesAsync` helper — unchanged.
- **Training-loop performance.** Static calls, no DI, no new allocation — identical cost to the helpers
  the loop already calls.
- **Hidden HTTP coupling.** Grep shows none inside rule bodies, but Phase 0's survey re-checks each
  method before it moves.
- **Scope creep.** Every temptation to "fix while moving" goes through the divergence rule or does not
  happen. The plan changes where code lives, not what it does.
