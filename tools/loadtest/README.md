# Capacity probe

Measures **at what load your Imperial 2030 deployment slows substantially**, so you can size the
rate limits (`RateLimiting:*`) and the replay caps (`MaxConcurrentSessions` /
`MaxSessionsPerOwner`) against real numbers instead of guesses. It ramps concurrency in steps and
reports latency percentiles, throughput and error breakdown at each step.

> **Only run this against a server you own or are authorised to test.** It sends sustained load.
> The script refuses to start without `--i-own-this`.

Standard library only — no `pip install`. Runs on the VPS itself (Python 3.8+).

## Run

```bash
# Gentle ramp of the lobby read (anonymous, multi-collection EF query)
python tools/loadtest/capacity_probe.py --base http://51.83.187.156 --scenario anon \
    --i-own-this --levels 5,10,20,40,80 --seconds 8
```

```bash
# The expensive path: each accepted replay/start spins up an in-memory DB + a background replay.
# This is the H4 vector — the one most able to hurt a SQLite box.
python tools/loadtest/capacity_probe.py --base http://51.83.187.156 --scenario replay \
    --i-own-this --levels 2,5,10,20 --seconds 8
```

```bash
# Everything, with a JSON report you can diff before/after a config change.
python tools/loadtest/capacity_probe.py --base http://51.83.187.156 --scenario all \
    --i-own-this --levels 5,10,25,50 --seconds 6 --json before.json
```

## Scenarios

| name     | request                              | why it matters |
|----------|--------------------------------------|----------------|
| `anon`   | `GET /api/games`                     | lobby list; `.Include()` across several collections, run for every game |
| `detail` | `GET /api/games/{id}`                | heaviest read: full game graph (players, nations, bonds, units, actions) |
| `guest`  | `POST /api/auth/guest-login`         | mints and HMAC-signs a JWT per call; also exercises the `auth` rate-limit policy |
| `replay` | `POST /api/games/{id}/replay/start`  | most expensive: in-memory DB + background replay per accepted call; exercises the H4 caps and the `replay` rate-limit policy |

`detail` and `replay` need a finished game id — the script auto-discovers one from `/api/games`,
or pass `--game-id`.

## Reading the output

Each concurrency level prints one line:

```
conc   40 | RPS   312.4 | p50    18 p95    74 p99   210 ms | 2xx  2499 429     0 5xx    0 fail    0
```

- **RPS** — successful (2xx) requests per second. When this stops rising as concurrency climbs,
  you have saturated something.
- **p50 / p95 / p99** — latency of successful requests, milliseconds. The probe flags a level
  `<- p95 Nx baseline` when tail latency has blown out relative to the server's at-rest speed.
- **2xx** served · **429** rate-limited · **5xx** server error · **fail** timeout / connection
  refused.

The distinction that matters:

- **429 rising** = the rate limiter is doing its job. That is a *healthy* result — the server is
  shedding load cheaply instead of melting. The summary reports the concurrency at which 429s
  first appear.
- **5xx / fail rising, or p95 exploding** = actual degradation. The summary calls this the
  **knee** — the concurrency where latency crosses ~4× baseline or errors appear. That is the
  number "how much to slow it down substantially" is really asking for.

If you see the knee *before* any 429s, the limits are set too loose for this hardware (or aren't
deployed) — every request is reaching the expensive work. If you see 429s well before the knee,
the limits are protecting the box, which is the goal.

## Turning findings into config

Everything below is read at startup from configuration (env vars on the VPS, per
`linux-deploy.info.txt`) — no rebuild needed:

| Setting | Default | Governs |
|---|---|---|
| `RateLimiting:AuthPermitLimit` / `:AuthWindowSeconds` | 20 / 60s | `/api/auth/*` requests per caller |
| `RateLimiting:ReplayPermitLimit` / `:ReplayWindowSeconds` | 10 / 60s | `replay/start` requests per caller |
| `Replay:MaxConcurrentSessions` | 20 | replay sessions held server-wide |
| `Replay:MaxSessionsPerOwner` | 5 | replay sessions held by one caller |
| `Replay:IdleTimeoutMinutes` | 30 | how long an unwatched session squats before eviction |

The rate limits bound how fast sessions can be *created*; the session caps bound how many can be
*held* at once. If the `replay` scenario reaches a knee below the concurrency at which 429s appear,
lower `ReplayPermitLimit`.

**If real users get "You already have the maximum number of replay sessions open"**, the per-caller
cap is being hit. A restart clears every in-memory session immediately:

```bash
sudo systemctl restart imperial2030
```

Then raise `Replay:MaxSessionsPerOwner`, and note that a load-test run against `replay` consumes
those slots for up to `Replay:IdleTimeoutMinutes` — so either lower that timeout while testing, or
restart afterwards rather than waiting it out.

### Setting them on the Linux VPS (systemd)

The config keys use `:` in code, but environment variables cannot contain a colon, so on Linux
the hierarchy separator is a **double underscore** (`__`):

| Config key | Environment variable |
|---|---|
| `RateLimiting:AuthPermitLimit` | `RateLimiting__AuthPermitLimit` |
| `RateLimiting:AuthWindowSeconds` | `RateLimiting__AuthWindowSeconds` |
| `RateLimiting:ReplayPermitLimit` | `RateLimiting__ReplayPermitLimit` |
| `RateLimiting:ReplayWindowSeconds` | `RateLimiting__ReplayWindowSeconds` |
| `Replay:MaxConcurrentSessions` | `Replay__MaxConcurrentSessions` |
| `Replay:MaxSessionsPerOwner` | `Replay__MaxSessionsPerOwner` |
| `Replay:IdleTimeoutMinutes` | `Replay__IdleTimeoutMinutes` |

They go in the systemd unit as `Environment=` lines, alongside the existing `Jwt__Key` (see
`linux-deploy.info.txt`). Only add the ones you want to change; anything omitted keeps its default.

```bash
sudo nano /etc/systemd/system/imperial2030.service
```

Under `[Service]`:

```ini
Environment=RateLimiting__AuthPermitLimit=10
Environment=RateLimiting__AuthWindowSeconds=60
Environment=RateLimiting__ReplayPermitLimit=5
Environment=RateLimiting__ReplayWindowSeconds=60
```

```bash
sudo systemctl daemon-reload && sudo systemctl restart imperial2030
```

`daemon-reload` is required — systemd caches the unit file, so a plain restart won't pick up the
edit. Confirm the values took and the service came back up:

```bash
systemctl show imperial2030 -p Environment
systemctl status imperial2030
```

These are read once at startup, so a restart is mandatory — there is no live reload. If the
service fails to start, check `journalctl -u imperial2030 -n 50`; note the server now hard-fails
at startup when `Jwt__Key` is missing, so rule that out before blaming the rate-limit lines.

## Important caveat for a single-VPS SQLite deployment

Behind a reverse proxy that does not rewrite the client address (nginx as commonly configured),
the rate limiter partitions every caller into **one shared bucket**, because it deliberately does
not trust `X-Forwarded-For` (a client can forge it). So from a single test machine you will see
the limits engage at the *aggregate* rate, and real clients would share that same budget. To get
true per-client limiting on the VPS, configure `ForwardedHeaders` with an explicit
`KnownProxies` / `KnownNetworks` allow-list so `RemoteIpAddress` becomes the real client address.
Until then, keep the global replay cap (`MaxConcurrentSessions`) as the real backstop — it is not
per-caller and protects the process regardless.

## Being a good neighbour to your own users

- Start with small `--levels` and short `--seconds`; step up only as needed.
- Run it against a quiet window — this competes with real players for the same SQLite file.
- `Ctrl+C` stops cleanly and still prints what completed.
- Prefer running it *from the VPS itself* or one machine, not many, so you are measuring server
  capacity rather than your own upstream bandwidth.
