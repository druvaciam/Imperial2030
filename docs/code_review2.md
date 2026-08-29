# Backend + Client Code Review (Round 2) — Imperial 2030

**Scope:** everything except `VueReplayViewer/` — `Server/` (13.8k lines), `Client/` (8.8k),
`Shared/` (1.2k), `Tests/` (11.3k), `Imperial2030.Functions/` (209), `python_rl/` (415).
**Date:** 2026-08-26 · **Branch:** `rl_bot` · **Baseline:** `dotnet build` clean, `dotnet test` **341/341** (324 at review time, plus 17 added by §S1).

Round 1 ([code_review.md](code_review.md)) covered `Server/` only. This round focuses on what that
review never looked at — the Blazor client, the RL Python pipeline, the Azure Functions app and the
test project — and re-checks `Server/` for what has changed since.

Findings are marked **[VERIFIED]** when reproduced or proven by mechanical scan, **[INSPECTION]** when
established by reading the complete code path. Game-mechanic claims cite `Imperial-2030-Rules.pdf` by
page, and where the engine (not the rulebook) decides an outcome, that is said explicitly.

---

## Status of round 1

Substantial progress, and in several places the fix chosen was better than the one suggested.

**Closed:**

| # | Finding | How |
|---|---|---|
| H1 | Guest login rejected by its own server | `OnTokenValidated` skips the store lookup for the `Guest` role |
| H2 | Committed JWT signing key | `JwtOptions` resolves once, fails fast, detects the leaked key by hash |
| H3 | No brute-force protection | Identity lockout + `RateLimitPolicies` |
| H4 | Unauthenticated replay-session exhaustion | Global + per-caller caps, admission before DB work |
| H5 | Unlimited production per turn | `HasProducedThisTurn` guard |
| M2 | Rondel move cost duplicated 10× | `RondelData.GetMoveCost` — **12 call sites, zero inline copies remain** [VERIFIED] |
| M6 | Fabricated rulebook quotes in `UpdateNationController` | Replaced with the real p.12 text, plus an unreachability proof for the tie-break branch and an explicit refusal to implement it speculatively |
| M8 | `.AsSplitQuery()` gaps | Fixed; `tools/scan_splitquery.py` reports clean |
| M10 | `ex.Message` leaked / `Console.WriteLine` | `ErrorResponses.Internal(TraceIdentifier)`; **0 `Console.WriteLine` left in `Server/`** [VERIFIED] |
| M11 | No real-auth test coverage | `RealAuthWebApplicationFactory` + auth test suites |
| L3 | `new Random()` join codes | `JoinCodeGenerator` using `RandomNumberGenerator.GetString`, with the modulo-bias trap documented |
| L4 | Unstable `List.Sort` for rankings | Replaced |
| L5 | Disk scan per bot-list call | `BotTypeCatalog` |
| L9 | Guest checks missing on `ManeuverController` | **Better than proposed:** a `NotGuestPolicy` authorization policy, rather than more scattered `IsInRole` calls |
| L2 | Raw Identity user IDs served anonymously | `UserIds` replaced by server-computed `IsCurrentUserInGame` / `IsCurrentUserHost` |
| M5 | Investor invested *after* Swiss Bank, reversing p.11 steps 2/3 | Card holder is queued first, then Swiss Bank players **rotated to begin at the card holder** per p.11's "in the order of play (clockwise), starting from the player currently with the Investor card"; FAQ p.14's no-double-dip guard cited inline |
| M7 | Last-unoccupied-factory entry protection missing | `ManeuverHelper.IsProtectedLastFactoryProvince`, enforced in `MoveArmy`/`MoveFleet` **and** in `ToggleHostility` — the latter closing a loophole round 1 did not even identify (enter peacefully, then stand the army upright) |

**Nothing from round 1 remains open.** The two items this review initially listed as outstanding were
both wrong, and are recorded here so a third review does not resurrect them:

- **M4 — not a defect; seating is randomised per game by design.** `GetOrderedPlayers` returns
  `OrderBy(p => p.Id)` over per-game `Guid`s, which is *stable within a game* and different between
  games. That satisfies every rulebook mechanic that references seating, because all of them need only a
  consistent circular order: the investor card's clockwise rotation (p.12), the Swiss Bank investment
  order counted from the card holder (p.11), and the control tie-break (p.12). "Seated to the left of"
  describes a physical table; randomising the seating each game is a legitimate — and arguably fairer —
  digital reading of it. Round 1 framed this as a missing feature; it is a design choice.
- **M9 — deliberately closed in round 1, and this review wrongly reopened it.** [code_review.md](code_review.md)
  already carries it as ⚠️ *proposal withdrawn*, with reasoning this round failed to consult: a
  `[Timestamp] RowVersion` protects SQL Server only, because EF Core does not generate rowversion values
  on SQLite — the default and VPS provider — so the token stays null and the check silently passes,
  producing something that looks like a guard and is not. A per-game lock is unsafe here specifically,
  because the bot's pacing delays sit *inside* its per-turn work, so any lock spanning a bot turn blocks
  human requests for the whole animation. No failing test could be produced for the race either: two
  interleaved `EndTurn` calls are benign, since last-write-wins yields one turn advance, which is the
  correct outcome anyway. It stays recorded as a known architectural risk, not a to-do.

One inconsistency was tidied as part of this review: `ManeuverController` used `NotGuestPolicy` while
`GamesController` carried 8 inline `IsInRole` checks. Both are now on the policy — see §S1.

---

## Executive summary

The server is in materially better shape than at round 1. The new problems are concentrated where
attention has not yet been: **the Blazor client**, which has had no review at all and contains the two
largest files in the repo, and **the cross-language RL boundary**, where a rule the project takes
seriously has no enforcement on the Python side.

Three themes:

1. **Client component lifecycle is unmanaged.** `GameMap.razor` schedules five separate delayed UI
   updates with no cancellation and implements no `IDisposable`; `GameRoom.DisposeAsync` can abort
   part-way and leak a SignalR connection.
2. **Errors are silently swallowed in the client.** `GameTerminal.razor` has 16 identical `catch { }`
   blocks around metadata deserialization — exactly the code CLAUDE.md calls "the single riskiest port".
   A malformed entry renders a blank line rather than anything diagnosable.
3. **The append-only RL state-vector rule (#17) is enforced on one side only.** `StateSize = 3172` and
   `TotalActionSize = 205` are hardcoded independently in C# and Python with no runtime check, so
   following rule #17 correctly in C# silently breaks training unless someone remembers Python.

**Highest-value single change:** add a state-size assertion to `imperial_env.py` (§P1). It is three
lines and it converts a silent training corruption into an immediate, obvious failure.

---

## Client

### C1 — `GameMap.razor` schedules delayed UI updates it can never cancel **[VERIFIED]** — ✅ FIXED

Five sites follow this shape ([GameMap.razor:1696](../Client/Components/GameMap.razor#L1696), 1738,
1772, 1822, 2788):

```csharp
_ = Task.Run(async () =>
{
    await Task.Delay(10000);
    if (recentMoves.Contains(move)) { recentMoves.Remove(move); await InvokeAsync(StateHasChanged); }
});
```

The component implements **no `IDisposable`** — confirmed by scanning every `.razor` in `Client/`:
`GameRoom` and `Lobby` implement disposal, `GameMap` does not, and it is the only one of the three
using timers without it.

So each of these holds a 5–10 second window in which the user can navigate away, after which
`InvokeAsync(StateHasChanged)` runs against a disposed component. The task is fire-and-forget (`_ =`),
so the resulting `ObjectDisposedException` is unobserved — no log, no error boundary, just a console
message in WASM. The map re-renders on every poll (every 400 ms during replay), so these accumulate.

**Action:** give `GameMap` a `CancellationTokenSource`, pass its token to `Task.Delay`, cancel it in
`Dispose()`, and check `token.IsCancellationRequested` before `InvokeAsync`. `GameRoom` already does
exactly this with `_nationRotationCts` / `_replayPollCts` — the pattern is in the codebase, this
component just does not use it.

### C2 — A throwing `InvokeAsync` in `GameRoom.DisposeAsync` skips the connection teardown **[INSPECTION]** — ✅ FIXED

[GameRoom.razor](../Client/Pages/GameRoom.razor):

```csharp
if (hubConnection is not null)
{
    await hubConnection.InvokeAsync("LeaveGameGroup", GameId.ToString(), MyPlayer == null);
    await hubConnection.DisposeAsync();
}
```

`InvokeAsync` throws when the connection has already dropped — which is the *common* case when leaving
a page, and certain on network loss. When it throws, `DisposeAsync()` on the next line never runs and
the connection leaks.

The author clearly knew the hazard: the replay-stop call immediately above is wrapped
`try { … } catch { /* best-effort — leaving the page shouldn't block on this */ }`. The hub call just
did not get the same treatment.

**Action:** wrap the `InvokeAsync` in its own try/catch, or move `DisposeAsync` to a `finally`. Leaving
the group is best-effort; disposing the connection is not.

Related, smaller: `Lobby.DisposeAsync` disposes its hub connection but never calls `LeaveGameGroup`, so
`PresenceTracker`'s observer counts rely entirely on `OnDisconnectedAsync` firing. That works, but the
two pages handle the same lifecycle differently for no stated reason.

### C3 — 16 silent `catch { }` blocks hide malformed action metadata **[VERIFIED]** — ✅ FIXED

`GameTerminal.razor` renders each log line by deserializing `action.Metadata` into one of ~10 shapes.
Every one of those is wrapped in a bare `catch { }` — 16 in the file, plus one `catch (TaskCanceledException) { }`
in `GameRoom` (that one is legitimate).

```csharp
try
{
    var meta = JsonSerializer.Deserialize<TaxationMetadata>(action.Metadata, …);
    if (meta != null) { <span class="message">@L["CollectedTaxes", …]</span> }
}
catch { }
```

When deserialization fails the line renders with a timestamp and a nation tag and **no message at all**.
Nothing is logged. This matters more here than it would elsewhere: CLAUDE.md calls this metadata mapping
"the single riskiest port", replay and import both depend on those shapes round-tripping, and
`project_replay_known_gaps` records that the re-logged action log can already diverge. A shape mismatch
is precisely the bug this code will hide.

It is also heavy duplication — 16 near-identical try/deserialize/render blocks in one if-chain.

**Action:** at minimum render a visible fallback (the raw `ActionType`, or a "could not read this entry"
marker) and `Console.Error` the exception, so a malformed entry is diagnosable instead of invisible.
Extracting the shape dispatch into a small formatter — the way `VueReplayViewer/src/log/formatAction.ts`
already does it — would collapse the duplication, and gives a pure function that is unit-testable
without mounting a component.

### C4 — User-controlled names rendered as raw HTML, safe only by an undocumented Identity default **[VERIFIED]** — ✅ FIXED

[GameRoom.razor:136](../Client/Pages/GameRoom.razor#L136) interpolates player names into a localized
string and renders the result as raw markup:

```csharp
var respondersNames = game.Players.Where(…).Select(p => p.UserName);
<p>@((MarkupString)L["Swiss_Waiting", string.Join(", ", respondersNames)].Value)</p>
```

`MarkupString` bypasses Blazor's HTML encoding. `p.UserName` resolves through `GetPlayerName` to either
a server-generated bot name or a **registered user's own username**.

**This is not currently exploitable**, and it is worth being precise about why: `Program.cs` does not
override `IdentityOptions.User.AllowedUserNameCharacters`, so Identity's default charset applies and it
excludes `<`, `>` and `"`. The protection is entirely incidental — nothing at the render site records
that it depends on an Identity default set in a different file, and `Register` performs no username
validation of its own beyond `[Required]`.

Loosening that charset is a routine, innocuous-looking change (allowing spaces or non-Latin names is a
common request, and this app is already localized into Belarusian). The moment someone makes it, this
becomes stored XSS against every player in the game room.

**Action:** HTML-encode the *arguments* rather than dropping `MarkupString` — the resource strings
legitimately contain markup, the interpolated names do not. Four other `MarkupString` sites take only
nation names from `Names.Nation(...)`, which is a closed enum-driven set and safe.

### C5 — Two components carry most of the client

`GameMap.razor` is **3,105 lines** and `GameRoom.razor` **2,713** — together 66% of the client. Both mix
markup, animation state, geometry, HTTP calls and game-rule interpretation in one file. This is not a
defect on its own, but it is why C1 and C3 went unnoticed, and it makes the client the only part of the
system with no automated tests at all.

**Action:** no rewrite. When touching these files, extract the pure parts — route/seam geometry,
metadata-to-message formatting, animation expiry — into plain classes that can be tested. The Vue
prototype already demonstrates the split.

---

## RL pipeline (`python_rl`)

### P1 — Rule #17's append-only guarantee is enforced on the C# side only **[VERIFIED]** — ✅ FIXED

`.agents/AGENTS.md` rule #17 requires state-vector changes to be *append-only*, with a size guard at
ONNX inference so older models keep running real inference. That guard exists in `RLBotStrategy`. The
Python side has no counterpart:

| | C# | Python |
|---|---|---|
| State size | `RLBotStrategy.StateSize = 3172` | `spaces.Box(shape=(3172,))` — hardcoded |
| Action size | `RLBotStrategy.TotalActionSize = 205` | `spaces.Discrete(205)` — hardcoded |

A repo-wide scan finds **no assertion anywhere in `python_rl/`** comparing the received observation to
the declared space. `export_onnx.py` derives `obs_dim` from `model.observation_space.shape[0]`, so it
inherits whatever the env declared rather than checking it.

The failure mode is the nasty one: append a float in C# (which rule #17 *encourages*), forget Python,
and the env now receives 3173 values into a space declared as 3172. Depending on the SB3 version this
either throws somewhere unhelpful or silently misaligns every feature index — training appears to run
and produces a worthless model.

**Action:** three lines in `imperial_env.py`, in both `reset` and `step`:

```python
expected = self.observation_space.shape[0]
if obs.shape[0] != expected:
    raise ValueError(f"Server sent {obs.shape[0]} floats, env expects {expected}. "
                     f"C# RLBotStrategy.StateSize changed - update this env (see AGENTS.md rule #17).")
```

Better still, have the `reset` response carry `stateSize` and `actionSize` from the C# constants and
assert against those, removing the second source of truth entirely. Worth adding to rule #17 itself,
since the rule as written only mentions the C#-side guard.

### P2 — The reconnect retry re-sends a step for a session the server has already discarded **[INSPECTION]** — ✅ FIXED

`ImperialEnv._send_receive` catches a dropped connection, reconnects, and replays the same payload:

```python
except (ConnectionError, BrokenPipeError):
    self.sock.close(); self._connect_socket()
    self.sock_file.write(json.dumps(data) + '\n'); self.sock_file.flush()
    return json.loads(self.sock_file.readline())
```

But `TcpTrainingServer` removes the session when its connection drops
([TcpTrainingServer.cs:208](../Server/Services/TcpTrainingServer.cs#L208):
`_sessions.TryRemove(currentSessionId, out var orphanedSession)`). So the retried `step` carries a
`sessionId` the server no longer knows — the retry cannot succeed.

The retry path is also less careful than the original: the first read guards `if not line: raise
ConnectionError(...)`, the retry calls `json.loads(readline())` directly, so a closed socket produces a
`JSONDecodeError` rather than a clear connection error. And a reconnect failure raises out of a bare
except-clause with no context.

To be clear about what this is *not*: it cannot double-apply an action, because the server discards the
session first. The cost is a confusing failure rather than a corrupted episode.

**Action:** on reconnect, either re-`reset` (starting a fresh episode and surfacing that to the caller)
or fail loudly with a message saying the session was lost. Retrying a `step` against a dead session is
guaranteed-useless work.

---

## Azure Functions (`Imperial2030.Functions`)

### F1 — Game and player names are interpolated into HTML email without encoding **[INSPECTION]** — ✅ FIXED

`GameNotifications` builds every email by string interpolation:

```csharp
string htmlContent = $"<h1>Game Started: {data.GameName}</h1>" +
                     $"<p><strong>Host:</strong> {data.HostName}</p>" +
                     $"<p><strong>Names:</strong> {string.Join(", ", data.PlayerNames ?? [])}</p>" + …
```

and sends it with `IsBodyHtml = true`.

`GameName` is fully user-controlled: `CreateGameRequest` constrains it to 50 characters with no charset
restriction, so it can contain arbitrary markup. Unlike the client case in §C4, **there is no incidental
protection here** — no charset filter applies to a game name.

The blast radius is small (recipients are the admin plus that game's own players, and most mail clients
strip scripting), but it is unambiguous HTML injection into a message sent to real people, and a
crafted name could forge convincing content — a fake "click here" link in an email that genuinely came
from your server.

**Action:** `System.Net.WebUtility.HtmlEncode` every interpolated value. The surrounding markup is
developer-authored; only the data needs encoding.

### F2 — Full exception text returned to the caller — ✅ FIXED

Both functions end with `response.WriteString(ex.ToString())` on the 500 path — full stack trace,
including file paths. This is the same defect as round 1's M10, which has since been fixed in `Server/`
but not here. The Functions app is a separate deployment, so it did not inherit the fix.

**Action:** log via `_logger` and return a generic body, matching what `ErrorResponses.Internal` now
does server-side.

### F3 — Personal address hardcoded as the admin recipient — ✅ FIXED

`private const string AdminEmail = "druvaciam@protonmail.com";` — a personal address compiled into the
binary and committed. Every SMTP setting beside it already comes from environment variables.

**Action:** read it from configuration like the rest (`ADMIN_EMAIL`), so it can differ per environment
and is not in source.

---

## Server — new observations

No round-1 item remains open. Two additions:

### S1 — Two mechanisms enforced the same guest rule — ✅ FIXED

`ManeuverController` uses `[Authorize(Policy = GameConstants.NotGuestPolicy)]`. `GamesController` still
has 8 inline `if (User.IsInRole(...)) return Forbid();` checks. The policy approach is better — it is
declarative, cannot be forgotten on a new endpoint, and is what the round-1 finding should have
recommended.

**Resolved.** All 8 inline checks removed; the same `[Authorize(Policy = GameConstants.NotGuestPolicy)]`
now sits on each of the 8 write actions (not the class — `GamesController` also serves
`[AllowAnonymous]` endpoints). `Tests/GuestPolicyCoverageTests.cs` was written *before* the refactor to
pin the existing behaviour, and asserts both halves for every endpoint: a guest gets 403, and a
registered user is neither 403 nor 401. A future write endpoint that forgets the policy now fails there
rather than silently admitting guests.

### S2 — `Program.cs` was doing real work — ✅ FIXED

It has grown a JWT resolution block, rate-limit registration, an Identity lockout configuration, a
`ReplaySessionManager` factory with three configuration bindings, and a startup data-migration block —
plus the pre-existing three-provider database selection. It is readable but it is now the file where
several unrelated policies meet.

**Resolved.** The 44-line seed/cleanup/backfill block became `Server/Data/StartupMaintenance.cs` and a
7-line call, leaving `Program.cs` as composition. Deliberately *not* a hosted service: this must finish
before the first request is served (the lobby reads `WinnerName`), whereas `IHostedService.StartAsync`
runs concurrently with the server coming up — calling it explicitly keeps that ordering visible. The
14-day retention became a named constant, and both passes stayed idempotent so a failure simply retries
next start.

---

## Tests

324 passing, no skipped tests, and the coverage added since round 1 is genuinely good — real-JWT
integration tests, relational split-query verification, and RL reward predicates extracted into pure
functions that are tested directly rather than through the TCP loop.

Two observations rather than defects:

- **The client has no tests at all.** Everything in §C1–C4 would have been caught by component tests, and
  §C3's formatter is a pure function over JSON that needs no DOM. bUnit would cover the first two; the
  third needs only xUnit.
- **`TestImportFromExportedJson` remains load-sensitive.** It waits for a randomised bot game inside a
  bounded loop; it now asserts the timeout distinctly (added in round 1's follow-up), so the next
  occurrence identifies itself, but it is still the one test whose failure mode is "the machine was
  busy".

---

## Worth keeping

Recorded so they are not "tidied" later by someone who does not know why they look odd.

- **`UpdateNationController`'s unreachable-branch comment.** It proves the tie-break branch cannot be
  reached, cites p.12 for the rule it would need, and explicitly declines to implement it speculatively,
  with instructions to write the failing test first if that ever changes. This is the right response to
  an underspecified path.
- **`ManeuverHelper.IsProtectedLastFactoryProvince` is enforced in `ToggleHostility`, not just on the
  move.** Guarding only the move would have left the rule trivially bypassable — enter the province
  peacefully, then stand the army upright afterwards. Round 1 missed that second door entirely; do not
  "simplify" the toggle check away.
- **`JoinCodeGenerator`** documents *why* `new Random()` was wrong on two counts and why
  `RandomNumberGenerator.GetString` avoids modulo bias.
- **`TaxationRules` / `RondelData.GetMoveCost`** — rules with a single home, cited to the rulebook page.
  `GetMoveCost` now has 12 callers and zero inline copies.
- **`tools/scan_splitquery.py`** makes rule #19 mechanically checkable and exits non-zero for CI.

---

## Recommended action plan

**Do first — silent-failure risks**

1. ~~§P1 Assert the observation size in `imperial_env.py`~~ — ✅ **done**. Went further than proposed:
   the server now reports `stateSize`/`actionSize` in the reset response, so `RLBotStrategy` is the
   single source of truth and the Python constants are a fallback the guard checks rather than a second
   authority. §P2 fixed alongside it.
2. ~~§C1 Give `GameMap` a `CancellationTokenSource` and `IDisposable`~~ — ✅ **done**, via a shared
   `ScheduleExpiry` helper that replaced all five copies of the pattern.
3. ~~§C2 Guard the `InvokeAsync` in `GameRoom.DisposeAsync`~~ — ✅ **done**.
4. ~~§C3 Render a visible fallback when metadata fails to parse~~ — ✅ **done** (`DescribeUnreadableEntry`).

**Then — correctness and hygiene**

5. ~~§F1 HTML-encode interpolated values in the notification emails~~ — ✅ **done** (10 body
   interpolations; subjects deliberately left as plain text).
6. ~~§F2 / §F3~~ — ✅ **done**; `ADMIN_EMAIL` is documented in `Imperial2030.Functions/info.txt` and
   unset now disables the admin copy instead of failing the notification.
7. ~~§C4 Encode the name arguments at the `MarkupString` site~~ — ✅ **done**.
8. ~~§S1 Move `GamesController` onto `NotGuestPolicy`~~ — ✅ **done**, with 17 tests pinning both halves.
9. ~~§S2 Extract the startup maintenance block from `Program.cs`~~ — ✅ **done** (`StartupMaintenance`).

**Nothing carried over from round 1** — every item is either fixed or deliberately closed. See the
status section above before reopening M4 or M9; both were re-raised in error by this review.

**Suggested rule change**

Rule #17 currently describes the C#-side size guard only. It should also require that a state-vector or
action-space change updates `python_rl/imperial_env.py` in the same commit, and that the env asserts the
received size — §P1 exists precisely because the rule's protection stops at the language boundary.
