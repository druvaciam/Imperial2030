# Backend Code Review — Imperial 2030

**Scope:** `Server/` (~12,700 LOC, excluding migrations), plus the `Shared/` contracts and EF Core data layer it depends on.
**Date:** 2026-08-24 · **Branch:** `rl_bot` · **Baseline at review time:** `dotnet build` clean, `dotnet test` 156/156 passing.

> **Status update — every High (§H1–§H5) is fixed**, plus §M8, with §M11 largely so. Each was reproduced
> with failing tests first, fixed, then re-verified against a live server or a real relational provider as
> well as in the suite. Suite now **187/187** — 156 pre-existing plus 31 new. The remaining open findings
> are all Medium and Low.
>
> **⚠️ §H2 changes deployment requirements** — see the warning in that section before deploying.


Findings marked **[VERIFIED]** were reproduced against a running server or proven by mechanical scan, not inferred from reading. Findings marked **[INSPECTION]** come from reading the complete relevant code path. Every game-mechanic claim is cited against `Imperial-2030-Rules.pdf` by page.

---

## Executive summary

The codebase is in better shape than its size suggests. Game rules are, where implemented, largely faithful to the rulebook — I checked taxation, final scoring, rondel movement cost, turn order, interest payout precedence and the trade-in mechanic against the PDF's own worked examples and they are all correct. `Shared/Constants/TaxationRules.cs` is genuinely exemplary work: it consolidates arithmetic that had drifted across five call sites, cites its rulebook page, and explains its derivations. The replay subsystem (`ReplaySessionManager`) is carefully built — cancellation, idle eviction, per-viewer pacing.

The problems cluster in three areas that have received much less attention than game logic:

1. **Authentication and authorization are effectively unowned.** One feature (guest login) is completely broken in production and no test can see it, because the integration test harness replaces the entire auth pipeline with a stub. The JWT signing key has a hardcoded fallback committed to the repo. Login has no brute-force protection.
2. **Server-side action validation has a hole.** Production is the only per-turn action flag with no re-entry guard, making free unlimited unit production reachable by any player.
3. **Core rules are duplicated across parallel implementations** (human endpoint / bot service / RL training server / client), and they have already drifted. The rondel move-cost formula alone exists in ten places.

Two rules are missing outright, and two are implemented against rules that don't exist in the PDF — the "invented rule" failure mode that `.agents/AGENTS.md` rule #16 was written to catch.

**Highest-value single change:** fix the guest-login bug (§H1) and add real-auth integration coverage (§M11) — one is a live production outage, the other is why nobody noticed.

---

## High severity

### H1 — Guest login is completely broken; every guest token is rejected **[VERIFIED]** — ✅ FIXED

> **Resolved.** `OnTokenValidated` now returns early for principals carrying the `Guest` role, before the
> user-store lookup. The `"Guest"` literal was extracted to `GameConstants.GuestRole` so the token
> validator and `GamesController`'s eight authorization gates read the same constant and cannot drift
> apart again — that divergence was the root cause. Live re-verification: a guest token on
> `POST /api/games` now returns **403** (was 401), no-token still returns 401, and guest browsing still
> returns 200. Covered by `Tests/AuthenticationTests.cs`, which runs against the real JWT pipeline.


<details>
<summary>Original finding</summary>

`AuthController.GuestLogin` ([AuthController.cs:58](../Server/Controllers/AuthController.cs#L58)) mints a JWT whose `NameIdentifier` is a fresh random `Guid` that is deliberately **not** an `ApplicationUser` row. But `Program.cs`'s `OnTokenValidated` handler ([Program.cs:129](../Server/Program.cs#L129)) looks up *every* token's subject in the user store and fails the request when it isn't found:

```csharp
var user = await userManager.FindByIdAsync(userId);
if (user == null) context.Fail("User no longer exists.");
```

`context.Fail()` makes `JwtBearerHandler` return `AuthenticateResult.Fail`, so `[Authorize]` responds 401. Guests are 401'd on every authenticated endpoint.

**Reproduction (live server on a scratch port):**

| Request | Result |
|---|---|
| `GET /api/games` (anonymous, `[AllowAnonymous]`) | `200` — control, server healthy |
| `POST /api/auth/guest-login` | `200`, 640-char token issued |
| `POST /api/games` with that guest token | **`401`** |
| `POST /api/games` with no token at all | `401` — identical |

Server log shows `Token challenge: invalid_token` for the guest request. Since the token was signed by this same process seconds earlier, signature/issuer/audience/lifetime all pass — the only remaining failure point is the `OnTokenValidated` user lookup.

This also makes the eight `if (User.IsInRole("Guest")) return Forbid();` checks in `GamesController` dead code — the request never reaches them.

**Action:** skip the store lookup for guest principals. Cleanest is to gate on the role already present in the token:

```csharp
OnTokenValidated = async context =>
{
    if (context.Principal?.IsInRole("Guest") == true) return; // guests have no store row by design
    var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    ...
}
```

Add a regression test that exercises the **real** JWT pipeline (see §M11) and asserts a guest token yields `403` on `POST /api/games`, not `401`.

---

</details>
### H2 — Hardcoded JWT signing key committed to the repository — ✅ FIXED

> **⚠️ Deploying this change requires configuring `Jwt:Key` first, or the server will not start.**
> That is the intended behaviour — see the rotation note at the end of this section.
>
> **Resolved.** Both fallbacks are gone. `Server/Configuration/JwtOptions.cs` resolves the key once at
> startup and is registered as a singleton, so `AuthController` (issuance) and `Program.cs` (validation)
> now share one instance and cannot disagree. Issuer, audience and token lifetime moved there too — they
> had been literals repeated across three call sites, and a drift in any one would have made every issued
> token silently fail validation.
>
> Startup now refuses to run outside Development when the key is missing, under 32 bytes (HMAC-SHA256's
> minimum, measured in bytes rather than characters), or equal to the leaked default. The leaked key is
> detected by **SHA-256 hash**, so the compromised value is not reintroduced into a shipped binary.
> Development falls back to a *per-process random* key with a logged warning — no usable secret remains
> anywhere in the repository, and local `dotnet run` is unaffected apart from tokens not surviving a
> restart.
>
> Verified against a real server in each configuration:
>
> | Environment | `Jwt:Key` | Result |
> |---|---|---|
> | Production | unset | **refuses to start** — "Refusing to start rather than fall back to a shared default." |
> | Production | leaked legacy key | **refuses to start** — "It is no longer secret…" |
> | Production | `tooshort` (8 bytes) | **refuses to start** — "must be at least 32 bytes… is 8 bytes." |
> | Production | valid 32-byte key | starts; guest token → 403; no warning logged |
> | Development | unset | starts; logs the ephemeral-key warning |
>
> Covered by `Tests/JwtOptionsTests.cs` (10 cases).
>
> **Still required — operational, cannot be done from the repo:**
> 1. Set `Jwt:Key` as an Azure App Service application setting **before** the next deploy. The workflow
>    ([main_imperial2030.yml](../.github/workflows/main_imperial2030.yml)) configures no app settings and
>    no appsettings file contains the key, so unless it was set manually in the portal that deployment has
>    been signing tokens with the committed default. Generate with `openssl rand -base64 32`.
> 2. Rotate the key on the Linux VPS if it was ever left at the placeholder in
>    [linux-deploy.info.txt](../linux-deploy.info.txt) (now annotated as required).
> 3. Treat any token issued before rotation as forgeable; rotation invalidates all existing sessions,
>    which is the desired outcome here.

<details>
<summary>Original finding</summary>


The same literal fallback appears in two places — [Program.cs:104](../Server/Program.cs#L104) and [AuthController.cs:24](../Server/Controllers/AuthController.cs#L24):

```csharp
builder.Configuration["Jwt:Key"] ?? "ThisIsASecretKeyForImperial2030GameOnly!"
```

If `Jwt:Key` is ever unset in a deployed environment, the server silently signs and accepts tokens with a key that is public in git history. Anyone can then forge a token for any `NameIdentifier` and act as any user. The failure is silent — there is no startup warning distinguishing "configured" from "using the committed default".

**Action:** remove both fallbacks. Read the key once at startup and fail fast when it is missing or shorter than 32 bytes, outside Development:

```csharp
var jwtKey = builder.Configuration["Jwt:Key"];
if (!builder.Environment.IsDevelopment() && (string.IsNullOrEmpty(jwtKey) || jwtKey.Length < 32))
    throw new InvalidOperationException("Jwt:Key must be configured (>=32 chars) outside Development.");
```

Register the resolved key as a typed option so `AuthController` consumes the same value instead of re-reading configuration with its own duplicate fallback. Rotate the key on any environment that may have been running on the default.

</details>

---

### H3 — No brute-force protection on login — ✅ FIXED

> **Resolved** with two mechanisms, because neither covers the other's attack.
>
> **Account lockout.** `Login` now passes `lockoutOnFailure: true`, and `Server/Configuration/AuthSecurity.cs`
> configures Identity's lockout: 5 consecutive failures → a 15-minute lock. A successful sign-in resets
> the counter automatically, so ordinary mistyping never accumulates. The duration is deliberately short
> because lockout is itself a denial-of-service lever — anyone who knows a username can trip it.
>
> **Rate limiting.** A fixed-window limiter (20 requests/minute, partitioned by remote address) applied
> via `[EnableRateLimiting]` on `AuthController`, so it covers login, register (account-creation spam) and
> guest-login (unbounded token minting), while gameplay traffic is never throttled. Rejections return
> `429` with `Retry-After`. Limits are configurable (`RateLimiting:AuthPermitLimit` /
> `:AuthWindowSeconds`) with production-sane defaults. The limiter runs after `UseRouting` (so endpoint
> policy metadata resolves) but before `UseAuthentication`, so throttled requests are refused without
> doing token or password work first.
>
> The no-user-enumeration property is preserved: wrong password and unknown user still return the same
> generic `"Invalid login attempt."`. Only a genuinely locked account names the lockout — registration
> already discloses whether a username is taken, so this adds no new leak, and a locked-out user who is
> only told "invalid" would keep retrying and keep the lock alive.
>
> Verified live over a real TCP connection (the in-memory `TestServer` leaves `RemoteIpAddress` null, so
> the partition-key path is only genuinely exercised against a real socket):
>
> | Check | Result |
> |---|---|
> | 25 rapid logins from one IP | 20 × `400`, then `429` — exact budget |
> | Throttled response headers | `429 Too Many Requests`, `Retry-After: 60` |
> | 30 requests to `/api/games` | all `200` — gameplay unthrottled |
> | 5 wrong passwords, then the correct one | correct password refused: account locked for 15 min |
> | 4 wrong passwords | still the generic `"Invalid login attempt."` |
>
> Covered by `Tests/AuthHardeningTests.cs` (lockout, counter reset, no enumeration) and
> `Tests/AuthRateLimitTests.cs` (429 + `Retry-After`, gameplay exempt).
>
> **Known limitation, deployment-specific.** The limiter partitions on the transport-level remote address
> and deliberately does **not** trust `X-Forwarded-For`, since a client can set that header freely and
> would otherwise get a fresh bucket per request. Behind a reverse proxy that does not rewrite the
> connection address (nginx on the VPS), all callers therefore collapse into one shared budget — which is
> why the default limit is generous rather than tight. For true per-client limiting there, configure
> `ForwardedHeaders` with an explicit `KnownProxies`/`KnownNetworks` allow-list so `RemoteIpAddress`
> becomes the real client address. That is infrastructure-specific and is not configured in the repo.

<details>
<summary>Original finding</summary>


[AuthController.cs:44](../Server/Controllers/AuthController.cs#L44) passes `lockoutOnFailure: false`:

```csharp
await _signInManager.PasswordSignInAsync(request.UserName, request.Password, false, false);
```

Identity's lockout machinery is registered but never engaged, and there is no rate limiting anywhere in the pipeline (`Program.cs` adds no `AddRateLimiter`). Password guessing against `/api/auth/login` is unbounded and unlogged.

**Action:** pass `lockoutOnFailure: true` and configure `IdentityOptions.Lockout`. Add ASP.NET Core's built-in rate limiter with a strict fixed-window partition on `/api/auth/*`. Note the login handler also returns `BadRequest` for both bad-user and bad-password (good — no user enumeration); keep that property.

</details>

---

### H4 — Unauthenticated replay-session creation is a resource-exhaustion vector — ✅ FIXED

> **Resolved** with three layers, keeping the endpoint `[AllowAnonymous]` since the Vue viewer depends on
> that.
>
> **Global cap** — `ReplaySessionManager.MaxConcurrentSessions` (default 20). Past it, `429`.
>
> **Per-caller cap** — `MaxSessionsPerOwner` (default 5), keyed by remote address, so one client cannot
> consume the whole global budget and lock every other viewer out. Deliberately generous for the same
> reverse-proxy reason as §H3: behind a proxy that does not rewrite the connection address, viewers can
> collapse into one key. The global cap is what actually protects the process.
>
> **Rate limit** — a separate `replay` policy (10/min per caller, vs auth's 20) on `replay/start`. The
> caps bound how many sessions a caller may *hold*; they say nothing about churn, and start/stop/start
> stays under the cap forever while costing a full source-game load and reseed every cycle.
>
> Two details that mattered more than the caps themselves:
>
> - **Admission is decided before the endpoint touches the database.** `StartReplay` used to load the
>   source game and project its entire action log into DTOs first. Had the cap been checked after that,
>   every rejected request would still cost a multi-collection query and thousands of allocations — the
>   cap would protect memory while leaving the database open to the identical flood. There is a test for
>   the ordering specifically: at capacity, a request for a *nonexistent* game returns `429`, not `404`.
> - **Admission and reservation happen in one critical section.** Counting live sessions and then
>   inserting is not atomic on a `ConcurrentDictionary`, so concurrent requests could all observe "one
>   slot left". In-flight sessions are tracked as reservations and counted against both budgets, released
>   in a `finally` so a failed seed cannot permanently shrink capacity.
>
> Verified live over real TCP: 14 requests to `replay/start` returned 10 × `404` (through to the endpoint)
> then `429` with `Retry-After: 60`, while `guest-login` and `GET /api/games` were unaffected — the auth
> and replay budgets are separate partitions.
>
> Covered by `Tests/ReplayAdmissionTests.cs` (global cap, per-caller cap with a second caller still
> admitted, stop releases capacity, cap-before-database ordering) and `Tests/ReplayRateLimitTests.cs`.
>
> **Refactor note:** the rate-limiting plumbing moved out of `AuthSecurity` into
> `Server/Configuration/RateLimitPolicies.cs`, which now owns both policies and the shared partition key.
> A policy named `replay` living in a class called `AuthSecurity` would have been exactly the kind of
> misnamed grab-bag that invites future drift. `AuthSecurity` keeps account lockout only.
>
> **Not changed:** `IdleTimeout` stays at 30 minutes. The review suggested lowering it, but a session is
> kept alive by the viewer's own 400 ms polling, so 30 minutes of silence already means the tab is gone —
> and with admission control in place, squatting is bounded by the caps rather than by the timeout.

<details>
<summary>Original finding</summary>


`POST /api/games/{gameId}/replay/start` is `[AllowAnonymous]` ([GamesController.cs:696](../Server/Controllers/GamesController.cs#L696)). Each call to `ReplaySessionManager.StartReplayAsync` ([ReplaySessionManager.cs:151](../Server/Services/ReplaySessionManager.cs#L151)) allocates:

- a dedicated EF Core InMemory database,
- a long-lived `ApplicationDbContext` held for up to **30 minutes** (`IdleTimeout`),
- a background task replaying an entire finished game (a typical game is ~120 nation turns, hundreds to thousands of logged actions), each step running full EF queries,
- a `LatestSnapshot` holding a complete `GameDetailDto` including every action.

There is no cap on concurrent sessions, no per-caller limit, and no authentication. A loop of unauthenticated POSTs exhausts memory and CPU quickly. The manager is otherwise well-built — the idle sweep, cancellation and disposal are all correct; the gap is purely admission control.

**Action:** cap total live sessions (reject with `429` past the limit) and cap sessions per caller. Given `[AllowAnonymous]` is a deliberate choice for the Vue viewer prototype, partition by IP via the rate limiter rather than requiring auth. Consider lowering `IdleTimeout` — 30 minutes is generous for a session kept alive by 400 ms polling.

</details>

---

### H5 — Production can be executed unlimited times per turn (free units) **[VERIFIED]** — ✅ FIXED

> **Resolved.** `ExecuteProduction` now guards on `HasProducedThisTurn` (rejecting with
> `"Already produced this turn."`) and on `game.IsInvestorTurn`, matching the guards `BuildFactory` and
> `ExecuteImport` already had. The flag is still only set when units were actually created, so a nation
> whose factories were all blockaded may still retry later in the turn — existing behaviour, deliberately
> unchanged. Verified not to affect the bot or replay paths: neither calls this endpoint (`BotService`
> and `GameReplayService` construct units directly). Covered by two new tests in
> `Tests/GamesControllerTests.cs`.


<details>
<summary>Original finding</summary>

`ExecuteProduction` ([GamesController.cs:1477–1568](../Server/Controllers/GamesController.cs#L1477)) validates the game is in progress, the caller controls the acting nation, and the nation is on a Production slot. It then produces a unit at every eligible factory and sets `nationState.HasProducedThisTurn = true` — **but never reads that flag.** Calling the endpoint again immediately produces another full batch.

Mechanical scan of every `HasProducedThisTurn` reference in `Server/`:

| Site | Kind |
|---|---|
| `GamesController.cs:445` | DTO projection |
| `GamesController.cs:1380` | reset to `false` |
| `GamesController.cs:1555` | **set** to `true` |
| `Game.cs:79`, `BotService.cs:557`, `GameReplayService.cs:408,771` | set/reset |

No read-as-guard anywhere. Contrast the sibling flags, which both have explicit guards:

- `HasBuiltThisTurn` → [GamesController.cs:1738](../Server/Controllers/GamesController.cs#L1738) `if (nationState.HasBuiltThisTurn) return BadRequest("Already built factory this turn.");`
- `HasImportedThisTurn` → [GamesController.cs:1944](../Server/Controllers/GamesController.cs#L1944)

Production is the only one of the three missing it. Damage is bounded only by `NationData.GetMaxArmies/GetMaxFleets`, so a player can fill to their unit cap in a single turn instead of over many — a decisive advantage. Per the rulebook (p.7) Production is one action taken on landing, not a repeatable one.

`ExecuteProduction` is also missing the `game.IsInvestorTurn` guard that `BuildFactory` has ([GamesController.cs:1724](../Server/Controllers/GamesController.cs#L1724)), and does not check for a pending battle.

**Action:** add the guard, matching the existing wording, plus the two missing state guards:

```csharp
if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");
if (nationState.HasProducedThisTurn) return BadRequest("Already produced this turn.");
```

Per `.agents/AGENTS.md` rule #2, write the failing test first: drive a game onto a Production slot, `POST /production` twice, assert the second call is rejected and unit count is unchanged.

---

</details>
## Medium severity

### M1 — `PresenceTracker` grows without bound, keyed by unvalidated client input **[VERIFIED]** — ✅ FIXED

`GameHub.JoinGameGroup(string gameId, ...)` ([GameHub.cs:59](../Server/Hubs/GameHub.cs#L59)) takes an arbitrary string, never parses it as a `Guid`, never checks the game exists, and never checks the caller is a member. `GameHub` also carries no `[Authorize]`, so anonymous connections reach it.

That string becomes a permanent dictionary key in `PresenceTracker` ([PresenceTracker.cs:13–20](../Server/Services/PresenceTracker.cs#L13)), a singleton. Nothing in the class ever **removes** an entry:

- `_userConnections` — decremented to `0`, never removed. Every user who has ever connected leaves a permanent entry.
- `_gameObservers` / `_gamePlayers` — keyed by `gameId`, never removed, not even when a game is deleted. Each holds a nested per-user dictionary that is also never pruned.

So the tracker leaks steadily in normal operation, and an attacker can force unbounded growth directly.

**Action (done):**

- `JoinGameGroup` now returns early unless `gameId` parses as a `Guid` **and** names an existing game. Parsing alone would not have fixed it — fresh Guids are free to mint, so unbounded growth would have survived. `GameHub` takes an `IServiceScopeFactory` for the lookup, matching how `BotService` reaches the DB from a non-scoped context.
- `UserDisconnected` / `RemoveObserver` / `RemoveActivePlayer` now remove a key once its count hits zero rather than parking a permanent `0`, via a shared `DecrementOrRemove`. Removal is **value-matched** (`TryRemove(KeyValuePair)`) so a reconnect racing with a disconnect cannot have its live entry dropped.
- A game's per-user dictionary is dropped once empty, via `DropIfEmpty` — reference-matched removal, re-added if a concurrent join populated the very instance being removed.
- `DeleteGame` calls the new `PresenceTracker.RemoveGame(gameId)`. Nothing disconnects when a game is deleted, so the per-connection paths would never have reached those entries.

The leak was invisible through the public API — `GetOnlineUsers`/`GetObserverCount` filter zero-valued entries out, which is precisely why it went unnoticed — so the tracker gained three diagnostic counts (`TrackedUserCount`, `TrackedObserverGameCount`, `TrackedActivePlayerGameCount`) to make it assertable. 10 tests across `Tests/PresenceTrackerTests.cs` and `Tests/GameHubJoinValidationTests.cs`, all watched failing first. They cover the leak paths and the behaviour that must survive: a second browser tab keeps a user online, a remaining observer keeps the game entry, and a real game still registers presence normally.

**Not done — `[Authorize]` on the hub**, which the finding raised as a "consider". An anonymous connection still reaches `JoinGameGroup`, but it can no longer grow the tracker (both because unknown ids are rejected and because the tracker is only touched when there is a `userId`). What it *can* still do is join a real game's SignalR group and receive its broadcasts. Whether that is a leak or the intended spectator behaviour is a product question — `GetGames`/`GetGame` are themselves `[AllowAnonymous]` and the Vue replay viewer depends on anonymous read access — so it needs a decision rather than a guess, and is left open.

### M2 — The rondel move-cost rule is duplicated in ten places **[VERIFIED]** — ✅ FIXED

One rulebook sentence (p.6, "for each additional space past the first three … (1 + Power Factor)") is re-derived independently at:

`GamesController.cs:1319–1322, 2049, 2095` · `BotService.cs:256–259, 432–435, 1361, 1405` · `RLBotStrategy.cs:1008–1011` · `TcpTrainingServer.cs:1159–1162, 2026–2029` · `Client/Pages/GameRoom.razor:2036–2040`

All copies currently agree — I checked them against the rulebook's two worked examples (4 spaces at 11 power = 3M; Investor→Factory = 6M) and the formula is correct. But this is exactly the drift risk `TaxationRules` was created to eliminate, and one copy had already drifted in that earlier case.

**Action (done):** `RondelData` (already in `Shared/`, so the client consumes it too) gains three members following `TaxationRules`'s pattern — rules only, page cited, callers supply their own inputs:

- `PowerPerFactorPoint` / `GetPowerFactor(int power)` — the scoring-track factor.
- `GetMoveDistance(int fromSlot, int toSlot)` — the clockwise step count, itself duplicated at every one of these sites.
- `GetMoveCost(int? fromSlot, int toSlot, int power)` — the rule. A null `fromSlot` (marker not yet on the rondel) costs nothing, matching what every copy already did.

All **eleven** move-cost sites now call it: `GamesController` ×3, `BotService` ×4, `RLBotStrategy`, `TcpTrainingServer` ×2, `GameRoom.razor`. A re-scan for `Power / 5` across `Server/` and `Client/` returns only `TcpTrainingServer.cs:1395`, which is deliberately a `float` for RL reward shaping and is not this rule.

Two extras beyond the original scope, both the same rulebook sentence's Power Factor rather than the move cost: final scoring's `bond.Interest * (Power / 5)` was written out separately in `Game.cs` **and again** in `GameRoom.razor` (server and client scoreboards computing the same score independently), plus a third bare `Power / 5` in the rondel tooltip. All three now go through `GetPowerFactor`, so the `/5` cannot drift between them either.

`Tests/RondelMoveCostTests.cs` anchors the helper on p.6's own numbers — the Power Factor examples ("Power Factor for each nation is zero", "17 power points ... amounts to 3"), free distance, per-space cost, and the China 11-power/Investor worked example — rather than on whatever the eleven copies happened to agree on. Deliberately **not** validated inside `GetMoveCost`: the `MaxMoveDistance` legality check, which stays with the callers that need it and has its own error message.

This is a pure refactor, so the guard is the existing suite (221 green), not a new behavioural test — there was no bug to reproduce, which is exactly what the finding said.

### M3 — `BotService.ExecuteBotTurn` re-implements `MoveNation` — **duplication only; the claimed divergences are not bugs**

[BotService.cs:239–408](../Server/Services/BotService.cs#L239) is a second implementation of the rondel turn: cost calculation, Swiss Bank intercept, investor pass-through detection, maneuver-phase initialisation. Confirmed divergences from the human path:

| Behaviour | `MoveNation` (human) | `ExecuteBotTurn` (bot) | Verdict |
|---|---|---|---|
| Maneuver phase init | Auto-skips `Fleets`→`Armies`→`None` when the nation has no unmoved units of that type, and logs the skip ([GamesController.cs:1485–1504](../Server/Controllers/GamesController.cs#L1485)) | Unconditionally sets `ManeuverPhase.Fleets` ([BotService.cs:341](../Server/Services/BotService.cs#L341)) | Real, but **cosmetic** |
| Action-log player name | `controller.GetPlayerName(_context)` | `controller.BotName ?? "Bot"` | **Not a divergence** |
| Turn end | `EndTurn` endpoint, which blocks on pending battles | Direct `game.AdvanceTurn()` ([BotService.cs:396](../Server/Services/BotService.cs#L396)), no pending-battle check | **Unreachable** |

This section originally claimed the maneuver divergence "produces different persisted state *and* a different action log", i.e. the class of divergence that breaks replay. On investigation that is overstated on all three rows, and no failing test could be written for any of them:

- **Phase init.** The asymmetry is real, but it self-corrects inside the same `ExecuteBotTurn` call: `BotManeuver` walks the empty Fleets phase, ends it itself, and the nation finishes where the human path would. The log entries differ in *kind* (`AutoSkipPhase` vs `AutoEndPhase`) but both are engine-derived and already excluded from `TestImportFromExportedJson`'s comparison. The one window where the difference is observable is the `IsInvestorTurn` early return at [BotService.cs:349](../Server/Services/BotService.cs#L349), which persists `Fleets` for a fleetless nation — visible in the UI until the investor phase resolves, with no effect on legality or final state. `Tests/BotManeuverTests.cs` now pins the convergence so this cannot silently become real.
- **Player name.** `GetPlayerName` returns `BotName` when it is non-empty, and `"Bot"` when the player is a bot without one — exactly what `BotName ?? "Bot"` produces. The two agree for every value a bot player actually has; they differ only for `BotName == ""`, which no code path creates.
- **Turn end.** `EndTurn` does nothing beyond its guards, `game.AdvanceTurn()` and the log entry, so the bot is not skipping any state reset. Its missing pending-battle guard is covered by proxy: the bot only reaches `AdvanceTurn` when `CurrentManeuverPhase == None` ([BotService.cs:388](../Server/Services/BotService.cs#L388)), and a pending battle always leaves the phase on `Fleets` or `Armies` — `BotManeuver` returns early, before the line that clears the phase.

To be clear on one thing I checked and found **not** to be a bug: `ExecuteBotTurn` deducts `controller.Cash -= cost` without an affordability check, but `ChooseRondelSlot` already filters unaffordable and over-distance slots ([BotService.cs:430,439](../Server/Services/BotService.cs#L430)) and its fallback slot is always distance-1 (cost 0). Bots cannot go cash-negative through this path.

**Action:** none required for correctness — nothing here justifies touching shared logic (`.agents/AGENTS.md` rule #21). The finding stands only as a maintainability one: two implementations of the rondel turn have to be kept in step by hand, and the phase-init row shows they already drifted once, harmlessly. If it is ever worth paying down, extract the shared body of `MoveNation` into a helper (`RondelMoveHelper.ApplyMove`) that both the endpoint and `BotService` call, keeping HTTP concerns in the controller — as a pure refactor, with the existing tests as the guard, not as a bugfix.

### M5 — Investor and Swiss Bank invest in the wrong order **[VERIFIED]** — ✅ FIXED

Rulebook p.11 numbers the Investor turn's steps: **2. Activating the Investor** (card holder gets 2M, then invests) then **3. Investing as Swiss Bank**. `HandleInvestorPhase` ([GamesController.cs:1162–1185](../Server/Controllers/GamesController.cs#L1162)) builds the queue the other way round — Swiss Bank players first, card holder appended last:

```csharp
eligibleInvestors.AddRange(swissBankPlayers);
if (game.InvestorCardHolderId.HasValue && !eligibleInvestors.Contains(...))
    eligibleInvestors.Add(game.InvestorCardHolderId.Value);
```

Bonds are a scarce shared pool and the trade-in mechanic makes first pick materially valuable, so this changes outcomes. The Swiss Bank players are also not ordered "starting from the player currently with the investor card" — they come out in plain `GetOrderedPlayers` order, not rotated to begin at the card holder.

Two things here are **correct** and should not be touched: the Swiss Bank definition (players controlling zero nations — p.12) and the `!Contains` guard preventing the card holder from also investing as a Swiss Bank (FAQ p.14, "Can the investor invest twice if he owns a Swiss Bank? No.").

Unlike §M3, §M4 and §M6, this one survives scrutiny on every axis. It is reachable (a player who loses every government gets a Swiss Bank — p.12 says so explicitly), it is observable (`ActingPlayerId` acts now and `PendingInvestorIds` drains one at a time, [GamesController.cs:1733–1736](../Server/Controllers/GamesController.cs#L1733)), and it changes outcomes rather than presentation.

**Action (done):** the card holder is now queued first, Swiss Bank players after. Two tests in `Tests/InvestorInterestTests.cs`: one for the order (watched failing first — the Swiss Bank player was acting), one pinning FAQ p.14's "can the investor invest twice if he owns a Swiss Bank? No", since the fix rewrote the guard that enforces it.

`Tests/GamesControllerTests.cs`'s `SwissBank_PlayerWithoutNations_CanInvestAndGainControl` asserted the *old* order outright — *"Assert that P2 is the acting player because Swiss Bank players go first"* — with no rulebook basis. Its actual subject (a Swiss Bank player can outbid and take over a nation) is unaffected, so it was resequenced: the card holder now takes their turn and passes first. This is a case of the test encoding the invented rule, not of bending logic to fit a test — see `.agents/AGENTS.md` rule #24.

**Also fixed:** p.11's ordering of *several* Swiss Bank players — "in the order of play (clockwise), starting from the player currently with the Investor card" — which was emitting them from the head of `GetOrderedPlayers` with no rotation. The play order is now rotated to begin at the card holder. A third test covers it, watched failing first (the two Swiss Banks came out exactly reversed).

This does not reopen §M4. It introduces no new ordering concept: `GetOrderedPlayers` is already what the codebase treats as order of play, including for p.12's "the Investor card moves clockwise to the next player" (`GetNextPlayerId`), so rotating *within* that order is strictly closer to the rulebook however the underlying order is defined. Whether that order should be a real seat index remains §M4's (withdrawn) question and is orthogonal. The inclusive/exclusive ambiguity in "starting from the player with the Investor card" also cannot bite here: the card holder is already queued as the Investor, so the Swiss Bank pass skips them either way (FAQ p.14).

### M6 — `UpdateNationController` cites two rules that don't exist — ✅ FIXED (comments only; **downgrade to Low**, the behaviour was never wrong)

[GamesController.cs:1227–1304](../Server/Controllers/GamesController.cs#L1227) contains two quoted "rules", neither of which exists in `Imperial-2030-Rules.pdf`:

- line 1249: *"If the sum of proper credits of several players is equal, the player among them who bought a bond of the nation most recently gets the card."* — the phrases "proper credits" and "most recently" appear nowhere in the PDF; a full-text search of all 16 pages returns no such rule.
- line 1283: *"Imperial 2030 Rule: 'If there is a tie, the player who already held the Governance card retains it.'"* — the word "Governance" appears nowhere in the PDF either; the game's term is **nation flag card** (p.12: *"he takes over the government of that nation and is given the nation flag card"*).

The actual rule (p.12): *"If, due to the allocation of bonds, a new player has achieved the highest credit sum (a tie is not sufficient), he takes over the government of that nation... If several players achieve the same highest credit sum, the player first in seating order, counting from the player with the investor card, takes over the government."*

**The behaviour is correct; only the comments are wrong.** This was originally filed claiming the `ActingPlayerId`/`candidates[0]` fallback was a live bug. It is not, and the correction is worth recording because the reasoning generalises.

Both branches are right:

- Incumbent among the tied leaders → retains. Matches *"a tie is not sufficient"*.
- Incumbent **not** among the tied leaders → falls back to `ActingPlayerId`, then `candidates[0]` (`Dictionary` enumeration order, i.e. whichever tied player held the first matching bond row). That fallback is neither the rulebook's tie-break nor deterministic across an import round-trip — **but it is unreachable.**

Unreachable because the method runs after each *single* bond purchase, so exactly one player's credit sum changes per call, and the government always already holds the maximum (seeded that way at setup — `GameSetupHelper` assigns it from the nation's 2M bond holder — and preserved by every branch). With `M` the old maximum held by the government and `V` the buyer's new sum: `V > M` makes the buyer the sole candidate; `V == M` and `V < M` both leave the government among the candidates, so it retains. Nothing strands it off the maximum.

A test that "proves" the fallback wrong has to hand `UpdateNationController` a state the game cannot produce — and it takes bond denominations that don't exist to do it (`BondData.AvailableCosts` is 2/4/6/9/12/16/20/25/30, all unique per nation). That is a proof about dead code, not about the game.

What *is* real is the citation defect, and it is not cosmetic: the first fabricated quote is what produced the `ActingPlayerId` branch — "whoever just invested wins the tie" is a direct implementation of a rule this game does not have. It happens to sit in unreachable code, but it is precisely the failure mode `.agents/AGENTS.md` rule #16 exists to prevent, and the comments *sound* authoritative, which is what makes them dangerous.

**Action (done):** both comments replaced with the real p.12 text, plus a note recording why the else-branch is unreachable and that the rulebook's seat-order tie-break should not be implemented speculatively — if a future change can strand the government off the maximum, write the failing test first. **The logic is untouched**; the diff is comments only. `Tests/TieBreakerTests.cs` gains one test pinning the reachable branch (incumbent tied for the lead retains the government), so nobody breaks *that* while editing nearby.

### M7 — Missing rule: a nation's last unoccupied factory province cannot be entered by hostile armies — ⚠️ **the rule is NOT missing**; a real bypass was found and is ✅ FIXED

Rulebook p.10: *"If a nation has only one factory left that is not occupied by hostile armies (standing upright), the province of this factory may not be entered by hostile armies. Armies of other nations that enter this province are laid down on their sides."*

**"A repo-wide search for any implementation of this protection returns nothing" is wrong.** It is implemented in four places, and implemented *correctly* — counting the owner's factories that are not occupied by hostile foreign armies, exactly as p.10 scopes it: `MoveFleet` and `MoveArmy` in `ManeuverController`, and both maneuver loops in `BotService`. The controllers reject the request (`"Must enter peacefully"`); the bots follow the rulebook's own wording more literally and force `isHostileMove = false` ("laid down on their sides"). Both reach the same board state.

**What was genuinely missing is the sub-claim about `ToggleHostility`, and that is a real bypass.** Entry is guarded, but nothing stopped a player entering peacefully — which is what the rule *forces* — and then standing the same army upright afterwards. The end state is precisely what p.10 forbids: a hostile army in the nation's last working factory province, blockading it out of production, taxation, factory building and rail.

Related, in `DestroyFactory` ([ManeuverController.cs:735–745](../Server/Controllers/ManeuverController.cs#L735)): `defenderFactoryCount` counts *all* the defender's factories, but the p.10 exception is scoped to factories *"not occupied by hostile armies"*. With the entry protection absent, this backstop is also too permissive.

**Action (done):** the rule now lives once, as `ManeuverHelper.IsProtectedLastFactoryProvince`, and `ToggleHostility` consults it before standing a unit upright. The two character-identical inline copies in `ManeuverController` were folded onto the same helper (behaviour unchanged — the helper reproduces their logic, including excluding the unit whose own hostility is being decided, which those call sites need because they assign `TerritoryId` before checking). `BotService`'s two copies were left: they evaluate *before* moving the unit, so they must not exclude it, and they force peace rather than rejecting — different enough that folding them in is a separate refactor, not a free one. Test written first and watched failing.

**`DestroyFactory`'s count — also ✅ FIXED.** This was initially left alone on the mistaken belief that the rule was circular (that destroying required occupying). It does not: armies lying on their sides can raze a factory — occupying and being present are different states, and the UI exposes destruction as its own action. So the p.10 exception is coherent and the count really was wrong.

The divergence is concrete. A nation holds two factories, A already blockaded and B working. Counting *all* factories sees two, decides the nation can spare one, and allows **B** — its last working factory — to be razed, leaving it with nothing it can produce or tax from. Counting unoccupied factories shields B and allows A, which is what p.10 says. `DestroyFactory` now uses the same `IsProtectedLastFactoryProvince` helper as the entry checks; the two rules turn out to be the same test.

Together with the entry protection these compose into the invariant the rulebook is clearly after: **a nation always retains at least one unoccupied factory.** You may occupy any factory that is not its last working one, and destroy any factory that is already occupied, but the last working one can be neither entered hostilely nor destroyed.

One existing test, `DestroyFactory_Fails_LastFactory`, went red on this change and was corrected rather than the fix being weakened: `Unit.IsHostile` defaults to `true`, so it had unwittingly seeded three *hostile* armies in Europe's only factory province — a board that cannot legally arise, because hostile entry there has always been refused. Setting `IsHostile = false` makes the setup legal and the test asserts exactly what it always meant to.

### M8 — `.AsSplitQuery()` missing on three multi-collection queries in the replay hot loop **[VERIFIED]** — ✅ FIXED

> **Resolved.** All three `GameReplayService` sites now call `.AsSplitQuery()` and `await …FirstAsync()`
> instead of a blocking `.First()`. Four further sites in `Tests/` were fixed too, so the rule is now
> mechanically clean rather than clean-except-for-known-exceptions — which matters, because a scan with
> standing known-ignorable hits has to be re-triaged by hand every time.
>
> **The scan is now a checked-in tool:** `tools/scan_splitquery.py`, referenced from rule #19 and exiting
> non-zero on any violation so it can gate CI. Two corrections to it while fixing this: `context.Games.Add(...)`
> is not a query (the original scan matched it and swept forward into the *next* statement's includes,
> producing 3 of its 10 reported hits as false positives — so the real count was 7, not 10), and an
> explicit `.AsSingleQuery()` now counts as compliant since it states the intent deliberately.
>
> **Verification gap this exposed, and closed.** Every replay test in the suite runs on the EF InMemory
> provider, where `AsSplitQuery` is a no-op — so the existing 33 passing replay tests proved *no
> regression* but said nothing about whether these query shapes are correct once actually split, which is
> the only situation where the change does anything. `Tests/SplitQueryRelationalTests.cs` closes that
> against real SQLite: it asserts both navigations load correctly with no cross-game leakage and no
> join-duplicated rows, that the query really does fan out into separate `SELECT`s, and (by contrast)
> that `AsSingleQuery` emits one joined statement.
>
> **One unresolved observation, stated plainly.** `TestImportFromExportedJson` failed once during this
> work. It did not reproduce in 13 further full-suite runs — 8 more with the change applied, 5 with it
> reverted — so the evidence does not attribute it to this fix, and equally does not clear it. The
> mechanism I did identify is pre-existing and unrelated to splitting: the test waits for a *randomised*
> bot game to finish inside a bounded loop, and that loop exits identically whether the game finished or
> the wait expired, so a load-induced timeout surfaced as a bare `Assert.Equal(Finished, InProgress)`
> further down. The test now asserts that distinction explicitly with a message saying which happened, so
> the next occurrence identifies itself instead of looking like an export/import defect.

<details>
<summary>Original finding</summary>


A mechanical scan of every `_context.Games` query chain in `Server/` (counting collection `.Include()`s and checking for `.AsSplitQuery()` in the same chain, per `.agents/AGENTS.md` rule #19's instruction not to trust a manual grep) found exactly three violations, all in `GameReplayService`:

| Site | Collections included |
|---|---|
| [GameReplayService.cs:337](../Server/Services/GameReplayService.cs#L337) | `Players`, `NationStates` |
| [GameReplayService.cs:396](../Server/Services/GameReplayService.cs#L396) | `NationStates`, `Players` |
| [GameReplayService.cs:1125](../Server/Services/GameReplayService.cs#L1125) | `Players`, `NationStates` |

All three run **once per replayed action** — thousands of times per import or replay session. All three also use synchronous `.First()` rather than `FirstAsync()` inside `async` methods, blocking a thread-pool thread on DB I/O each time (`GameReplayService` has 86 synchronous LINQ terminal calls in total).

Everything under `Server/` outside these three is compliant. The scan also flagged 10 sites in `Tests/`, which matter less (InMemory provider) but will still emit the EF warning.

**Action:** add `.AsSplitQuery()` to the three sites and convert them to `FirstAsync()`. Keep the scan script — it is the right tool for re-verifying this rule.

</details>

---

### M9 — No optimistic concurrency control on `Game` — ⚠️ **proposal withdrawn**; the real defect found nearby is ✅ FIXED

The model snapshot shows `IsConcurrencyToken()` only on Identity's own `ConcurrencyStamp` columns — no token on `Game` or any game entity. Meanwhile the same game row is mutated concurrently by: HTTP endpoints, `BotService`'s fire-and-forget background loop (`TriggerBotTurn` → `Task.Run`), and SignalR-driven client actions.

`BotService` guards with a static `ConcurrentDictionary` gate (`_activeBotGames.TryAdd`, [BotService.cs:88](../Server/Services/BotService.cs#L88)) which serialises *bot* work per game, but nothing serialises bot work against a concurrent human request. `StartGame` uses `ExecuteUpdateAsync` for its lobby transition ([GamesController.cs:936–943](../Server/Controllers/GamesController.cs#L936)) — that pattern is correct and worth generalising.

**Action — partially done, and the original proposal is withdrawn.** Both remedies suggested here are worse than they look:

- **`[Timestamp] byte[] RowVersion` protects SQL Server only.** EF Core does not generate rowversion values on SQLite, so every token stays null and the check silently passes — and SQLite is this app's *default and VPS* provider (`Program.cs`). The result would be protection on Azure that reads as protection everywhere. A portable version needs an app-maintained `Guid` token plus a `SaveChangesAsync` override, after which `DbUpdateConcurrencyException` surfaces at ~30 write sites; unhandled that is a 500 storm strictly worse than a rare lost update.
- **A per-game async lock is unsafe in this codebase specifically.** The bot's pacing delays (`BotUnitActionDelay`, `BotDelayMs`) sit *inside* its per-turn work, not between turns, so any lock spanning a bot turn blocks human requests for the whole watchable animation. Making it safe means threading lock release through every bot delay helper.

No failing test could be produced for the persistence-level race either. The obvious candidate — two interleaved `EndTurn` calls — turns out to be *benign*: last-write-wins yields one turn advance, which is what two concurrent end-turns should produce anyway. So the database half is left alone deliberately, recorded here as a known architectural risk with the SQLite caveat, rather than "fixed" with something that looks like a guard and is not.

**What was real and is ✅ FIXED — a dropped bot wakeup.** Bots have no clock; they act only when `TriggerBotTurn` reports a change. `_activeBotGames` allows one loop per game, and a caller finding one running returned immediately. Fine while that loop is still working — but a loop that has just decided it has nothing left to do (`if (!botActed ...) break`) still holds the slot until its `finally`. A request landing in that window was discarded by the caller **and** never seen by the loop: nobody running, nobody coming. The game then waits forever on a bot move — e.g. the battle response a human's hostile move just asked two bot nations for.

The slot is now paired with a wakeup latch: every caller records the request *before* claiming the slot, each pass consumes the mark at its start, and a loop re-checks for a fresh mark before exiting instead of stranding it. The narrow window between that last check and the slot's release is closed by re-triggering once after release; this cannot spin, because an unprompted pass consumes the mark and leaves without setting it. 2 tests in `Tests/BotWakeupLatchTests.cs` (the first verified failing when the recording is moved back after the slot claim), using internal test seams so the behaviour is assertable without racing a live background loop.

### M10 — Exception detail returned to clients **[VERIFIED]** — ✅ FIXED

Three endpoints return raw exception text in a 500 body: [GamesController.cs:689](../Server/Controllers/GamesController.cs#L689) (`ImportGame`), [GamesController.cs:994](../Server/Controllers/GamesController.cs#L994) (`StartGame`), [ManeuverController.cs:832](../Server/Controllers/ManeuverController.cs#L832) (`NextPhase`). This can leak connection strings, file paths and internal structure.

Related: 10 `Console.WriteLine` calls remain in `Server/` (including inside the JWT event handlers), bypassing the NLog file sink that `Program.cs:18–20` sets up specifically so all diagnostics land in `logs/`.

Both halves verified before fixing: three `return StatusCode(500, …ex.Message…)` sites, and exactly 10 `Console.WriteLine` calls in `Server/`.

**Action (done):**

- All three endpoints now log via `ILogger` and return `ErrorResponses.Internal(HttpContext.TraceIdentifier)` — a fixed message plus the request's trace id as a quotable reference. New `Server/Helpers/ErrorResponses.cs` holds that text so the three cannot drift apart.
- All 10 `Console.WriteLine` calls replaced with structured `ILogger` calls, so they reach the NLog file sink like everything else. `Server/` now contains zero `Console.WriteLine`. `BotService` already had a logger; `GamesController` and `ManeuverController` take one as an **optional trailing constructor parameter** (defaulting to `NullLogger`) so the ~35 direct `new XController(...)` constructions in `Tests/` did not have to change, while DI still supplies the real logger in production. `RLBotStrategy` takes one the same way, since it is constructed by bot-type name rather than resolved from DI; `BotService` passes its own logger in.
- `GetAvailableBotTypes` became an instance method so its catch can log — both call sites were already instance methods and it depends on no static state.

Tests: `NextPhase_WhenTheHandlerThrows_DoesNotReturnExceptionDetailToTheClient` (watched failing first — the body was literally `"Sequence contains no matching element"`), plus `ErrorResponsesTests` on the shared message. Only `NextPhase` has a cheap deterministic throw trigger, so it is the one wired end-to-end; `ImportGame` and `StartGame` received the identical two-line change against the tested helper.

**Not changed, and worth a separate decision:** [GameReplayService.cs:1093](../Server/Services/GameReplayService.cs#L1093) and [ReplaySessionManager.cs:425](../Server/Services/ReplaySessionManager.cs#L425) also put `ex.Message` into a `ReplayStateDto.ErrorMessage` that an `[AllowAnonymous]` endpoint serves. That is the same class of exposure, but there the detail is deliberate — it is what the replay UI shows when a replay fails, and the replay tests print it for diagnosis. Suppressing it would need a way to keep the diagnostic without the leak, so it is left as an open question rather than silently changed.

### M11 — The real authentication pipeline has zero test coverage — ✅ FIXED

> **Resolved.** `Tests/RealAuthWebApplicationFactory.cs` swaps only the database and leaves production
> authentication intact, supplying `Jwt:Key` through configuration the way a real deployment does.
> `Tests/AuthenticationTests.cs` covers guest issuance/acceptance, guest authorization, register → login
> → authorized call, missing token, and a token signed with the wrong key.
>
> Both items listed as still open are now closed. **Startup key validation** exists and is covered by
> `Tests/JwtOptionsTests.cs` — 9 tests over missing, blank, too-short and leaked-legacy keys outside
> Development, plus the Development fallback, its per-process uniqueness, and an explicitly configured key
> winning. That note was simply stale. **Token expiry** now has a test: a token signed with the host's
> REAL key and expired ten minutes ago must come back `401`, so the only thing wrong with it is the clock.
> It is paired with a control using the same forged token unexpired, which must reach authorization and
> return `403` — without that, a blanket `401` from any forged token would pass the expiry test for
> entirely the wrong reason.


<details>
<summary>Original finding</summary>

`CustomWebApplicationFactory` ([CustomWebApplicationFactory.cs:56–62](../Tests/CustomWebApplicationFactory.cs#L56)) registers a `Test` scheme as the default authenticate/challenge scheme, so no test ever exercises JWT validation, `OnTokenValidated`, role claims, or token expiry. This is why §H1 — a total outage of a user-facing feature — sits in a repo with 154 green tests.

**Action:** add a small integration test class that leaves the real auth pipeline in place (supplying a test `Jwt:Key` via configuration) and covers: register → login → authorized call; guest-login → authorized call; expired token; tampered signature.

---

</details>
## Low severity

| # | Finding | Location |
|---|---|---|
| L1 | ~~`OnTokenValidated` hits the user store on **every** authenticated request — an extra DB round-trip per call, on an endpoint the client polls every 400 ms during replay. Cache or drop it.~~ — ✅ **FIXED**: memoised behind `UserExistenceCache` (30s TTL, negative results cached too), so the check is kept but no longer costs a lookup per request. Polling confirmed at `GameRoom.razor:1514` and `useReplay.ts`'s `POLL_INTERVAL_MS`. 4 tests. | [Program.cs](../Server/Program.cs), [UserExistenceCache.cs](../Server/Services/UserExistenceCache.cs) |
| L2 | ~~`GetGames` is `[AllowAnonymous]` and returns `UserIds` — raw ASP.NET Identity user GUIDs — for every game to every anonymous caller.~~ — ✅ **FIXED**: `UserIds` and `HostId` are gone from `GameDto` entirely, replaced by `IsCurrentUserInGame` / `IsCurrentUserHost` computed server-side for the caller. Every client use was already a comparison against the caller's *own* id (`UserIds.Contains(currentUserId)`, `HostId == currentUserId`), so nothing needed another player's id — 5 call sites in `Lobby.razor`/`GameRoom.razor` updated. `HostId` was the same leak the finding did not name, in the same DTO on the same endpoint, so it went too. Note `GameDetailDto : GameDto`, so the anonymous `GetGame` inherited the exposure and is covered as well. Mirrored into `VueReplayViewer/src/api/dtos.ts` per the repo convention (it carried both fields but read neither); `vue-tsc` and its 22 tests pass. New test asserts a host sees both flags, a non-host player sees one, and a stranger — the same path an anonymous caller takes — sees neither. | [GameDto.cs](../Shared/Models/GameDto.cs), [GamesController.cs](../Server/Controllers/GamesController.cs) |
| L3 | ~~`GenerateJoinCode` uses `new Random()` per call rather than `RandomNumberGenerator`.~~ — ✅ **FIXED**: extracted to `JoinCodeGenerator.Generate()`, backed by `RandomNumberGenerator.GetString`, which also samples the 36-character alphabet without the modulo bias a naive `bytes[i] % 36` would introduce (256 is not a multiple of 36, so A–D would come up slightly more often). 3 tests: shape, no repeats across 500 rapid successive calls — the failure mode a per-call time-seeded generator has — and full alphabet coverage, which would catch a weak source silently shrinking the keyspace. | [JoinCodeGenerator.cs](../Server/Helpers/JoinCodeGenerator.cs) |
| L4 | ~~`GetRankedPlayers` uses `List.Sort` with a comparison returning `0` for absolute ties… Use `OrderBy`/`ThenBy` (stable).~~ — ✅ **FIXED**, with one correction to the reasoning: `List.Sort` is not *random*, so it does not vary run-to-run within a process. What it does is leave the order of equal elements **unspecified**, and in practice its introsort actively scrambles them — verified by shimming the old sort back in, where 20 absolutely-tied players came out in an order unrelated to the roster. That matters because a finished game and its import rank tied players from differently-ordered rosters, and `TestImportFromExportedJson` asserts `WinnerName` matches exactly. Now `OrderBy` (documented stable), so ties keep roster order. Deliberately **not** `ThenBy(p => p.Id)`: p.6's chain ending in an absolute tie is a case the rulebook does not settle, and inventing a GUID-order winner is what `.agents/AGENTS.md` rule #22 forbids — the result is made repeatable, not decided. 3 tests, plus the existing `TieBreakerTests` guarding that the p.6 chain still outranks roster position. | [Game.cs](../Server/Models/Game.cs) |
| L5 | ~~`GetAvailableBotTypes` performs `Directory.GetFiles` on every call, including from the `[AllowAnonymous]` `available-bots` endpoint and every `AddBot`. Cache at startup.~~ — ✅ **FIXED**: extracted to a `BotTypeCatalog` singleton that discovers once behind a `Lazy` (`ExecutionAndPublication`, so racing requests still scan only once). The models ship with the deployment and cannot appear at runtime — a new one arrives with a new deployment, which restarts the process. Injected as an optional ctor arg so the ~21 direct `new GamesController(...)` calls in `Tests/` were untouched. 6 tests, none of which existed before: built-ins always offered, `RL` recognised from either default model name, extra `RL-*` models listed while unrelated `.onnx` files are ignored, no duplicate `RL`, a missing directory degrading to built-ins only, and the cache proven by deleting a model from disk and asserting it is still reported. | [BotTypeCatalog.cs](../Server/Services/BotTypeCatalog.cs) |
| L6 | ~~`PreviewTaxation` and `ApplyTaxation` each contain their own copy of the bonus-calculation block. They agree today; extract the shared branch.~~ — ✅ **FIXED**: the block (identical character-for-character, both variant and standard branches) is now `TaxationRules.ComputeSuccessBonus`, cited to p.12 and covered by 12 chart-anchored tests. The **revenue/soldiers-pay preamble** was duplicated the same way and is now `ComputeTaxNumbers` — worth folding in, since a preview whose numbers can drift from the apply it previews is the whole point of the pair. Both methods dropped from ~35 to ~20 lines. | [TaxationHelper.cs](../Server/Helpers/TaxationHelper.cs), [TaxationRules.cs](../Shared/Constants/TaxationRules.cs) |
| L7 | ~~Duplicated dead statement: `if (game == null) return NotFound();` appears twice in a row.~~ — ✅ **FIXED**: the second copy removed (it had drifted to `:1327–1328`). Confirmed by scanning the whole file for adjacent identical lines rather than trusting the stale line numbers — this was the only such pair. | [GamesController.cs](../Server/Controllers/GamesController.cs) |
| L8 | ~~`ToggleHostility` accepts any unit type… toggling a fleet is meaningless.~~ — ⚠️ **the proposed fix would have changed nothing, and chasing it uncovered a larger bug that is ✅ FIXED.** `Unit.IsHostile` defaults to **true**, so fleets are hostile without anyone toggling; restricting the toggle to armies would not have altered a single outcome. Fleet hostility *did* matter, because three consumers don't filter by `UnitType.Army` — rail suspension ([ManeuverHelper.cs:225](../Server/Helpers/ManeuverHelper.cs#L225), [:326](../Server/Helpers/ManeuverHelper.cs#L326)) and the last-factory protection ([:403](../Server/Helpers/ManeuverHelper.cs#L403)) — both of which p.9/p.10 scope to armies. **But the root cause is that fleets could be on foreign land at all:** `MoveFleet` rejected only land→land, so a fleet in the North Atlantic could sail into Berlin or London (verified: the endpoint returned `Ok` and the fleet landed in Berlin). p.8 — *"Once fleets are at sea, they cannot return to a land region"* — forbids it, and `BotService`/`TcpTrainingServer` have always filtered fleet destinations to sea regions; only the human endpoint did not. Fixed at the root: a fleet's destination must be a sea region, which leaves its own-harbour first move working (also p.8) and staying put untouched. With no foreign fleets on land, the three unfiltered consumers can no longer misfire, so they are left alone rather than patched. The **toggling in own/neutral territory** half is genuinely cosmetic: neutral regions have no owner, so the occupancy helper returns early, and nothing else reads it. | [ManeuverController.cs](../Server/Controllers/ManeuverController.cs) |
| L9 | ~~`ManeuverController` has `[Authorize]` but none of the `IsInRole("Guest")` checks `GamesController` uses.~~ — ✅ **FIXED**: new `GameConstants.NotGuestPolicy` (registered in `Program.cs`) applied controller-wide, since every action there is a game move; `GamesController` stays per-action because some of its endpoints are deliberately guest-readable. The "unreachable today" reasoning was confirmed (`JoinGame` refuses guests, so they never become a `Player`) — but a guest **did** reach the handler before this, returning 404 rather than 403. Also replaced the 8 bare `"Guest"` literals in `GamesController` with `GameConstants.GuestRole`; the constant's own doc-comment warns against exactly that split, and the literals had survived it. 2 integration tests: guest → 403, registered user → still 404, i.e. not locked out. | [ManeuverController.cs](../Server/Controllers/ManeuverController.cs), [Program.cs](../Server/Program.cs) |
| L10 | ~~`Game.PendingInvestorIds` deserializes JSON on every property read and returns a **new** list each time, so `game.PendingInvestorIds.Add(x)` silently does nothing.~~ — ✅ **FIXED**, and better than the suggested method pair: it is now a plain `List<Guid>`, mapped by EF as a primitive collection exactly like its two neighbours `PendingBattleDefenders` and `PendingSwissBankResponders`. The `[NotMapped]` accessor and `PendingInvestorIdsJson` backing field are gone, so the trap cannot be re-entered rather than being documented around. Three list-shaped properties on one entity, two of which accepted `.Add()` and one of which silently ignored it, is a coin flip for whoever touches it next. Migrations for **both** providers (rule #9) are a bare `RenameColumn` — data-preserving, confirmed by dumping the two on-disk formats first: both are JSON arrays of GUID strings differing only in case (`System.Text.Json` lowercases, EF uppercases), and `Guid.Parse` is case-insensitive. A test seeds a row in the **old** format via raw SQL and asserts it still reads back, so the upgrade cannot silently empty an in-flight investor queue. | [Game.cs](../Server/Models/Game.cs), [GameCollectionPersistenceTests.cs](../Tests/GameCollectionPersistenceTests.cs) |
| L11 | ~~`PlayerHelper.GetPlayerName` falls back to a synchronous `context.Users.FirstOrDefault(...)`… `BuildGameDetailDto` calls it per player, per bond and per nation state.~~ — ⚠️ **the named consequence is wrong, but a real one sits next door; the real one is ✅ FIXED.** `BuildGameDetailDto` has exactly two callers — `GetGame` and `ReplaySessionManager.CaptureSnapshotAsync` — and **both already `.ThenInclude` User on all three paths** (Players, NationStates→Controller→User, Bonds→Holder→User), so the fallback never fires there and there is no N+1. Where it *did* fire is `ManeuverController`: all 7 of its queries loaded `Players` without `User`, and every action-logging call then paid a blocking `AspNetUsers` round-trip — proven with a SQLite SQL-counting test (1 before, 0 after) on `ToggleHostility`. All 7 now `.ThenInclude(p => p.User)`; since the queries are already `.AsSplitQuery()`, this joins into the existing Players fetch rather than adding a round-trip. Note replay and bots are unaffected either way: `GetPlayerName` returns `BotName` first, which is populated for both. **Still open:** ~15 `Include(g => g.Players)` in `GamesController` are likewise missing `User`, and several of those endpoints do log a human actor — same one-query-per-call cost, but once per *turn* rather than once per *unit move*, so they were left rather than swept blind. | [ManeuverController.cs](../Server/Controllers/ManeuverController.cs), [PlayerNameQueryCountTests.cs](../Tests/PlayerNameQueryCountTests.cs) |
| L12 | ~~`TriggerBotTurn` is fire-and-forget `Task.Run` with no continuation; `TryPlayBotTurnAsync` rethrows after logging, producing unobserved task exceptions.~~ — ✅ **FIXED**: the `Task.Run` body now catches and logs **with the game id**, so a failure is observed where it happens instead of surfacing as an `UnobservedTaskException` at GC (and being swallowed by default). The inner `catch { log; throw; }` was removed rather than kept — it logged without the game id and rethrew into a void, so it added a duplicate entry and no information; the `finally` that releases the bot-loop slot is untouched, and the direct callers (all tests) still see the exception. Matters more than a typical logging nit: an unobserved bot crash is indistinguishable from a bot with nothing to do, the same silent-stall symptom §M9's wakeup latch addresses. Test asserts the logged message names the game — it failed against the old log, which did not. | [BotService.cs](../Server/Services/BotService.cs) |

---

## Verified as correct (no action needed)

Recording these so they aren't re-litigated. Each was checked against the rulebook's own worked examples, not assumed.

- **Final scoring** — `Game.CalculateScore` uses `bond.Interest * (Power / 5)` plus cash. Matches p.6 exactly, including the worked example (17 power → factor 3; 5M interest × 3 = 15M). Tie-break by credit sum (`bond.Cost`) in power-rank order also matches p.6.
- **Taxation** — revenue, soldiers' pay, success bonus and power gain all match p.12 and the p.13 worked example (revenue 11 → bonus 2M, +4 power).
- **The `VariantBonusOnlyForTaxIncreases` variant** — `GetPowerGain(new) - GetPowerGain(old)` looks wrong at a glance but is right: the power-gain tiers *are* the tax chart's column indices, so the difference equals "fields the marker moves right" (p.13). Verified against the p.13 example: 7 → 12 gives 5 − 1 = 4M.
- **Rondel move cost** — `(distance − 3) × (1 + Power/5)`, max 6 spaces, paid by the controller from personal cash. Matches p.6 and both its examples.
- **Nation turn order and skipping** — `AdvanceTurn` follows the `Nation` enum (Russia→China→India→Brazil→USA→Europe, p.7) and skips uncontrolled nations, as p.7 requires.
- **Investor interest precedence** — others paid before the controller; controller forgoes their own interest and covers the shortfall personally. Matches p.11.
- **Bond trade-in** — pays only the difference into the treasury (p.11).
- **Swiss Bank double-dip prevention** — the card holder cannot also invest as a Swiss Bank (FAQ p.14).
- **`TcpTrainingServer` binds to `IPAddress.Loopback`**, not `Any` — the training port is not network-exposed.
- **Migration parity** — SQLite's `InitialCreate` is a later squashed baseline; both providers are in sync for every migration after 2026-08-13.
- **EF primitive collections** — `PendingBattleDefenders` / `PendingSwissBankResponders` map via `PrimitiveCollection<string>`, which carries a proper value comparer, so in-place `.Clear()`/`.Add()` is change-tracked correctly.
- **`Shared/Constants/TaxationRules.cs`** — the reference example for how rules should live in this codebase.

---

## Recommended action plan

**Do first — production correctness and security**

1. ~~§H1 Fix guest login~~ — ✅ **done** (fix + `GameConstants.GuestRole` + 5 real-JWT tests).
2. ~~§H2 Remove both hardcoded JWT key fallbacks; fail fast at startup~~ — ✅ **done** (`JwtOptions` +
   10 tests). **⚠️ Set `Jwt:Key` in Azure App Service before the next deploy — the server now refuses to
   start without it. Rotate any key that may have been the committed default.**
3. ~~§H5 Add the `HasProducedThisTurn` guard~~ — ✅ **done** (+ `IsInvestorTurn` guard, 2 tests).
4. ~~§H3 Enable Identity lockout and add rate limiting on `/api/auth/*`~~ — ✅ **done** (`AuthSecurity` +
   5 tests). Optional follow-up: configure `ForwardedHeaders` with a `KnownProxies` allow-list on the VPS
   so the limiter partitions per real client instead of per proxy.
5. ~~§H4 Cap concurrent replay sessions and rate-limit `replay/start`~~ — ✅ **done** (global + per-caller
   caps, `replay` rate-limit policy, 6 tests).

**Do next — durability and correctness of shared state**

6. ~~§M11 Add real-JWT integration tests~~ — ✅ **mostly done**; token-expiry coverage still open.
7. §M1 Validate `JoinGameGroup`'s `gameId`; make `PresenceTracker` actually evict.
8. ~~§M8 Add `.AsSplitQuery()` + `FirstAsync` at the three `GameReplayService` sites~~ — ✅ **done**
   (+ 4 `Tests/` sites, `tools/scan_splitquery.py` checked in and wired into rule #19, 3 relational tests).
9. §M10 Stop returning `ex.Message`; finish the `Console.WriteLine` → `ILogger` migration.

**Then — rules fidelity (each needs a failing test first, per rule #2)**

10. §M7 Implement the last-unoccupied-factory entry protection; scope `DestroyFactory`'s count.
11. §M5 Reorder Investor before Swiss Bank; anchor Swiss Bank order at the card holder.
12. ~~§M6 Replace the two fabricated rule comments~~ — ✅ **done**; the tie-break logic itself needed no
    change, the disputed branch is unreachable (see the section).

**Ongoing — structural**

13. §M2 Extract `RondelData.GetMoveCost`; replace all ten call sites.
14. §M3 Extract the shared rondel-move body so `BotService` and `MoveNation` cannot diverge.
15. §M9 Add `RowVersion` to `Game` and handle concurrency conflicts.
17. Work through the Low table opportunistically.

**Suggested new project rule** (`.agents/AGENTS.md`) — items §M2/§M3 keep recurring, and rule #21's "don't bend business logic" is silent on duplication:

> **Rules Live In One Place, And `Shared/` Is Where They Live.** Before writing arithmetic or a decision derived from `Imperial-2030-Rules.pdf`, search for an existing helper in `Shared/Constants/`. If the rule is already implemented elsewhere, call it — do not re-derive it, even inline and even if it is one line. If it isn't, add it there (rules only; each caller supplies its own inputs), cite the rulebook page, and replace every existing copy in the same change. `Shared/Constants/TaxationRules.cs` is the reference example. Human endpoints, `BotService`, `TcpTrainingServer`, `RLBotStrategy` and the Blazor client must all consume the same implementation — a rule with N copies has N chances to drift, and the copies do not fail loudly when they disagree.
