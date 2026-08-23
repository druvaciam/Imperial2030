# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A web implementation of **Imperial 2030**, a rebalanced variant of the "Imperial" board game with six
nations (Russia, China, India, Brazil, USA, Europe) — NOT the classic 2006 "Imperial" (which has
Austria-Hungary/Italy/France/Germany/Great Britain/Russia). The authoritative rules are
`Imperial-2030-Rules.pdf` at the repo root; verify game-mechanic questions against it rather than
general "Imperial"-family assumptions or existing code (existing code can itself be a bug — check the
PDF before treating either the request or the current behavior as ground truth).

Also includes an RL bot that trains via self-play against the C# game engine and runs natively via ONNX,
and a standalone Vue replay viewer.

## Repository layout

Blazor WebAssembly "hosted" app (`Server` hosts `Client`, both reference `Shared`) plus a few
independent pieces:

- **`Server/`** — ASP.NET Core Web host: REST API (`Controllers/`), SignalR hub (`Hubs/GameHub.cs`),
  business logic (`Services/`, `Helpers/`), EF Core data access (`Data/`), Identity/JWT auth, and the RL
  training TCP server (`Services/TcpTrainingServer.cs`).
- **`Client/`** — Blazor WebAssembly UI (`Pages/`, `Components/`, `Shared/`).
- **`Shared/`** — DTOs, models, and game constants (`Constants/`) referenced by both `Server` and
  `Client`, and mirrored by hand into the Vue viewer's TypeScript types (see below).
- **`Tests/`** — xUnit test suite covering `Server` (integration tests via `Microsoft.AspNetCore.Mvc.Testing`
  and unit tests), referencing `Server` and `Shared` directly.
- **`Imperial2030.Functions/`** — separate Azure Functions app (isolated worker) for push notifications;
  deployed independently of `Server`.
- **`python_rl/`** — Python side of the RL pipeline (Stable-Baselines3/PPO via Gymnasium), trains against
  `Server` over TCP and exports trained models to ONNX for `Server` to consume natively.
- **`VueReplayViewer/`** — standalone Vue 3 + TypeScript + Vite SPA, a separate prototype frontend that
  consumes `Server`'s `[AllowAnonymous]` `/api/games*` endpoints to replay finished games. In dev it proxies
  `/api` to the Server's HTTPS dev port (see `VueReplayViewer/vite.config.ts`); it is not built/served by
  `Server` itself. Its `src/api/*.ts` interfaces are hand-mirrored copies of `Shared/Models/*.cs` — when a
  `Shared/Models` DTO/metadata type changes, update the matching interface in `VueReplayViewer/src/api/`
  too. Note the metadata JSON is PascalCase (raw `System.Text.Json.JsonSerializer.Serialize`, bypassing
  MVC's camelCase default) while every other DTO is camelCase — see the comment in `src/api/actionMetadata.ts`.

## Common commands

### .NET (Server / Client / Shared / Tests)

```bash
# Build entire solution
dotnet build

# Run the server (serves the Blazor Client too), default port per launch settings
dotnet run --project Server/Imperial2030.Server.csproj
# HTTPS dev URL used by VueReplayViewer's proxy and .claude/launch.json: https://localhost:7115

# Run the server in RL training mode (starts TcpTrainingServer on port 5005, uses in-memory DB)
dotnet run --project Server/Imperial2030.Server.csproj -c Release -- --training

# Run all tests
dotnet test

# Run a single test (by fully-qualified name or filter expression)
dotnet test --filter "FullyQualifiedName~ManeuverHelperTests"
dotnet test --filter "TestRLBotWinRate"

# Publish (Windows deploy target, matches main_imperial2030.yml)
dotnet publish Server/Imperial2030.Server.csproj -c Release -o "<out>" /p:PublishSingleFile=false

# Publish (Linux VPS deploy target, matches linux-deploy.info.txt)
dotnet publish Server/Imperial2030.Server.csproj -c Release -o ./publish --self-contained -r linux-x64
```

CI (`.github/workflows/dotnet.yml`) runs `dotnet restore`, `dotnet build --no-restore`,
`dotnet test --no-build`. A second workflow (`main_imperial2030.yml`) builds/publishes/deploys `Server`
to Azure Web App on push to `main`.

### VueReplayViewer

```bash
cd VueReplayViewer
npm run dev       # vite dev server on :5173, proxies /api to the Server on :7115
npm run build      # vue-tsc -b && vite build
npm run test       # vitest run
```

### RL pipeline (python_rl)

```bash
cd python_rl
pip install -r requirements.txt

# Train (Server must already be running with --training, see above)
python train.py --bot-type RL
python train.py --bot-type RL-2 --opponents RL,Default,Random   # train against specific opponents
python train.py --bot-type RL-2 --reset                          # ignore existing checkpoints

# Export a trained model to ONNX, then copy into Server/ (and add a <Content Include> entry
# in Imperial2030.Server.csproj if it's a new --bot-type name)
python export_onnx.py --bot-type RL
cp RL.onnx RL.onnx.data ../Server/
```

## Architecture notes

### Dual EF Core database providers

`Server/Data/` (and per-provider `Server/Migrations/SqliteMigrations` /
`Server/Migrations/SqlServerMigrations`) supports three swappable EF Core backends selected in
`Program.cs`: in-memory (training mode), SQLite (default/VPS), and SQL Server (Azure). All three are
kept behind the shared `ApplicationDbContext`. **Any new persistent property on an EF Core entity needs
migrations generated for both `SqliteApplicationDbContext` and `SqlServerApplicationDbContext`** — see
`.agents/AGENTS.md` rule on entity model changes. Migrations apply automatically on startup
(`DbSeeder.SeedAsync` / `Database.MigrateAsync()`), never via manual `dotnet ef database update`.

### Queries with multiple `.Include()` collections

Any query against `Games` (or another aggregate root) with `.Include()` on two or more *collection*
navigations (`Players`, `NationStates`, `TerritoryStates`, `Bonds`, `Units`, `Actions`) must add
`.AsSplitQuery()` before the terminal materializing call, or EF Core produces a cartesian-product row
explosion. See `.agents/AGENTS.md` for the full rule and exceptions.

### Bot strategies

`Server/Services/Bots/Strategies/` implements `IBotStrategy` (multiple named strategies registered as
singletons in `Program.cs`: Default, Aggressive, Friendly, Greedy, Random, RL). `BotService.cs`
orchestrates bot turns. `RLBotStrategy.cs` loads an ONNX model via `Microsoft.ML.OnnxRuntime`, builds a
manually-normalized state tensor, runs inference, and applies an action mask to exclude invalid moves.

### RL training loop

`TcpTrainingServer.cs` (hosted service, only registered under `--training`) listens on TCP port 5005,
manages isolated in-memory game instances, and exchanges game state/actions/rewards with the Python
`imperial_env.py` Gymnasium environment. Trained PPO models (`.zip` + `vec_normalize.pkl`) are exported to
ONNX (`export_onnx.py`) and copied into `Server/` for zero-dependency native inference. **Backward
compatibility is mandatory**: state-vector or action-space changes must only *append* new floats/actions
at the end of the existing layout, and inference must guard on the loaded model's actual input/output
width so older `.onnx` models keep running real (not degraded/random) inference — see
`.agents/AGENTS.md` and `rlbot.info.txt` for the full rule and verification steps.

### Replay

`GameReplayService.cs` + `ReplaySessionManager.cs` reconstruct and step through a finished game's logged
`GameAction` entries (`Server/Models/GameAction.cs`, logged via `GameLogger.cs`) for both the Blazor
client and `VueReplayViewer`. Import reproduces final game state exactly, but the re-logged action log
can still diverge from the original on pending-battle-negotiation moves and a few derived log entries —
`TestImportFromExportedJson` guards the rest. When replay can't unambiguously reconstruct a historical
decision, extend the *replay-only* call site to consume information the log already recorded (e.g. an
existing `BattleTargetNation`/`BattleTargetUnitType` field) — never narrow what live game logic is
allowed to decide just to make replay deterministic.

## Working conventions (see `.agents/AGENTS.md` for full detail)

These are durable project rules, not suggestions — the file has 24 numbered rules with concrete examples;
skim it before touching game logic, RL code, or EF Core entities. Highlights:

- Test-driven backend bugfixes: write a failing test first, watch it fail, fix, watch it pass.
- Build the project and run targeted tests during development; run the full `dotnet test` suite before
  declaring a task done.
- Never change shared business logic (`Server/Controllers`, `Server/Services`, `Server/Helpers`) to make a
  test or new feature more convenient — only for a provable, real gameplay bug, cited explicitly against
  `Imperial-2030-Rules.pdf`. Equally, don't reflexively refuse a fix by calling it "existing game logic"
  without first checking whether it actually matches the rulebook.
  Don't "fix" an underspecified tie-break just to make a replay/test reproduce one specific outcome.
- Prefer existing domain-meaningful properties (`Nation`, `UnitType`, `IsHostile`, territory IDs) over
  adding new persisted fields/entities/IDs.
  Deliver every part of a multi-part request, or explicitly say what wasn't done — don't silently drop or
  narrow scope.
