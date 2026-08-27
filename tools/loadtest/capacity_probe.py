#!/usr/bin/env python3
"""
Capacity probe for an Imperial 2030 deployment — measures at what load the server slows
substantially, rather than just flooding it.

This is a MEASUREMENT tool for testing YOUR OWN server. It ramps concurrency in steps and
reports latency percentiles, throughput and error rate at each level, so you can find the
"knee": the point where added load stops buying throughput and starts buying latency. That
number is what tells you how aggressive the rate limits need to be.

It only exercises the anonymous / guest surface (no login, no real user data):

  anon    GET  /api/games                       lobby list; multi-collection EF query
  detail  GET  /api/games/{id}                  full game detail; the heaviest read query
  guest   POST /api/auth/guest-login            mints + signs a JWT each call
  replay  POST /api/games/{id}/replay/start     the expensive one: each accepted call spins up
                                                 an in-memory DB + a background replay task
                                                 (this is the H4 vector; needs a finished game)

Zero external dependencies — standard library only (threads + urllib), so it runs on the VPS
itself or anywhere with Python 3.8+.

USE ONLY AGAINST A SERVER YOU OWN OR ARE AUTHORISED TO TEST. Sending sustained load to
someone else's server is an attack. This refuses to start without --i-own-this.

Examples
--------
    # Start gentle: ramp the lobby read from 5 to 80 concurrent, 8s per step.
    python capacity_probe.py --base http://51.83.187.156 --scenario anon \
        --i-own-this --levels 5,10,20,40,80 --seconds 8

    # Probe the expensive replay-start path (auto-discovers a finished game id).
    python capacity_probe.py --base http://51.83.187.156 --scenario replay \
        --i-own-this --levels 2,5,10,20 --seconds 8

    # Compare every scenario in one run, writing a JSON report.
    python capacity_probe.py --base http://51.83.187.156 --scenario all \
        --i-own-this --levels 5,10,25,50 --seconds 6 --json report.json
"""
from __future__ import annotations

import argparse
import json
import random
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field


# --------------------------------------------------------------------------------------------
# One HTTP call, timed. Returns (status_code_or_0, elapsed_seconds, note).
# --------------------------------------------------------------------------------------------
def http_call(method: str, url: str, body: bytes | None, timeout: float) -> tuple[int, float, str]:
    req = urllib.request.Request(url, data=body, method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            resp.read()  # drain, so timing includes the full response body
            return resp.status, time.perf_counter() - start, ""
    except urllib.error.HTTPError as e:
        # A 429 is the rate limiter doing its job — a healthy rejection, not an overload. It is
        # counted separately below precisely so it is not confused with the server slowing down.
        return e.code, time.perf_counter() - start, ""
    except urllib.error.URLError as e:
        return 0, time.perf_counter() - start, f"url_error:{e.reason}"
    except TimeoutError:
        return 0, time.perf_counter() - start, "timeout"
    except Exception as e:  # noqa: BLE001 - any transport failure is a data point, not a crash
        return 0, time.perf_counter() - start, f"error:{type(e).__name__}"


# --------------------------------------------------------------------------------------------
# Scenarios: each yields (method, url, body) for one request.
# --------------------------------------------------------------------------------------------
class Scenario:
    def __init__(self, name: str, base: str, finished_game_id: str | None):
        self.name = name
        self.base = base.rstrip("/")
        self.finished_game_id = finished_game_id

    def request(self) -> tuple[str, str, bytes | None]:
        raise NotImplementedError


class AnonList(Scenario):
    def request(self):
        return "GET", f"{self.base}/api/games", None


class GameDetail(Scenario):
    def request(self):
        return "GET", f"{self.base}/api/games/{self.finished_game_id}", None


class GuestLogin(Scenario):
    def request(self):
        return "POST", f"{self.base}/api/auth/guest-login", b""


class ReplayStart(Scenario):
    def request(self):
        return "POST", f"{self.base}/api/games/{self.finished_game_id}/replay/start", b""


SCENARIOS = {
    "anon": AnonList,
    "detail": GameDetail,
    "guest": GuestLogin,
    "replay": ReplayStart,
}
NEEDS_GAME_ID = {"detail", "replay"}


# --------------------------------------------------------------------------------------------
# Results for one concurrency level.
# --------------------------------------------------------------------------------------------
@dataclass
class LevelResult:
    concurrency: int
    latencies_ok: list[float] = field(default_factory=list)  # seconds, 2xx only
    count_2xx: int = 0
    count_429: int = 0       # rate limited (healthy)
    count_5xx: int = 0       # server error (overload)
    count_other: int = 0     # 3xx/4xx that isn't 429
    count_fail: int = 0      # timeout / connection refused / reset
    wall_seconds: float = 0.0
    notes: dict[str, int] = field(default_factory=dict)

    @property
    def total(self) -> int:
        return self.count_2xx + self.count_429 + self.count_5xx + self.count_other + self.count_fail

    def pct(self, p: float) -> float:
        if not self.latencies_ok:
            return float("nan")
        ordered = sorted(self.latencies_ok)
        k = min(len(ordered) - 1, int(round((p / 100.0) * (len(ordered) - 1))))
        return ordered[k]

    @property
    def rps(self) -> float:
        return self.count_2xx / self.wall_seconds if self.wall_seconds > 0 else 0.0


def run_level(scenario: Scenario, concurrency: int, seconds: float, timeout: float) -> LevelResult:
    result = LevelResult(concurrency=concurrency)
    lock = threading.Lock()
    deadline = time.perf_counter() + seconds

    def worker() -> None:
        # Tiny jitter so N threads don't march in lockstep and create artificial bursts.
        time.sleep(random.uniform(0, 0.01))
        while time.perf_counter() < deadline:
            method, url, body = scenario.request()
            status, elapsed, note = http_call(method, url, body, timeout)
            with lock:
                if 200 <= status < 300:
                    result.count_2xx += 1
                    result.latencies_ok.append(elapsed)
                elif status == 429:
                    result.count_429 += 1
                elif status >= 500:
                    result.count_5xx += 1
                elif status != 0:
                    result.count_other += 1
                else:
                    result.count_fail += 1
                if note:
                    result.notes[note] = result.notes.get(note, 0) + 1

    started = time.perf_counter()
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        for _ in range(concurrency):
            pool.submit(worker)
    result.wall_seconds = time.perf_counter() - started
    return result


# --------------------------------------------------------------------------------------------
# Baseline: a single serial request, to know the server's "at rest" latency for this scenario.
# The knee is judged relative to this, not to an absolute millisecond figure.
# --------------------------------------------------------------------------------------------
def measure_baseline(scenario: Scenario, timeout: float, samples: int = 5) -> float:
    times = []
    for _ in range(samples):
        method, url, body = scenario.request()
        status, elapsed, _ = http_call(method, url, body, timeout)
        if 200 <= status < 300:
            times.append(elapsed)
        time.sleep(0.2)
    return statistics.median(times) if times else float("nan")


def discover_finished_game(base: str, timeout: float) -> str | None:
    method, url, body = "GET", f"{base.rstrip('/')}/api/games", None
    status, _, _ = http_call(method, url, body, timeout)
    if not (200 <= status < 300):
        return None
    try:
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            games = json.loads(resp.read())
    except Exception:  # noqa: BLE001
        return None
    finished = [g for g in games if g.get("status") == 2]
    return finished[0]["id"] if finished else None


def fmt_ms(seconds: float) -> str:
    return "   n/a" if seconds != seconds else f"{seconds * 1000:6.0f}"  # NaN check


def print_level(res: LevelResult, baseline_s: float) -> None:
    p50, p95, p99 = res.pct(50), res.pct(95), res.pct(99)
    slow = ""
    if baseline_s == baseline_s and p95 == p95 and baseline_s > 0:
        factor = p95 / baseline_s
        if factor >= 10:
            slow = f"  <== p95 {factor:.0f}x baseline"
        elif factor >= 4:
            slow = f"  <- p95 {factor:.1f}x baseline"
    degraded = res.count_5xx + res.count_fail
    print(
        f"  conc {res.concurrency:>4} | "
        f"RPS {res.rps:7.1f} | "
        f"p50 {fmt_ms(p50)} p95 {fmt_ms(p95)} p99 {fmt_ms(p99)} ms | "
        f"2xx {res.count_2xx:>6} 429 {res.count_429:>6} 5xx {res.count_5xx:>5} fail {res.count_fail:>5}"
        f"{slow}"
    )
    if degraded and res.notes:
        top = sorted(res.notes.items(), key=lambda kv: -kv[1])[:3]
        print("         notes: " + ", ".join(f"{k}={v}" for k, v in top))


def run_scenario(name: str, base: str, game_id: str | None, levels: list[int],
                 seconds: float, timeout: float) -> dict:
    scenario = SCENARIOS[name](name, base, game_id)
    print(f"\n=== scenario: {name} ===")
    baseline = measure_baseline(scenario, timeout)
    print(f"  baseline (serial) p50: {fmt_ms(baseline)} ms")

    level_results = []
    knee = None
    for conc in levels:
        res = run_level(scenario, conc, seconds, timeout)
        print_level(res, baseline)
        level_results.append(res)

        # Heuristic knee: first level where 2xx p95 crosses 4x the serial baseline, OR any
        # server errors / connection failures appear. 429s do NOT count — those are the limiter
        # working, and finding they kick in early is a GOOD result, reported separately below.
        if knee is None:
            p95 = res.pct(95)
            overloaded = res.count_5xx > 0 or res.count_fail > 0
            latency_knee = (baseline == baseline and p95 == p95 and baseline > 0 and p95 >= 4 * baseline)
            if overloaded or latency_knee:
                knee = conc

    # Where does the rate limiter first bite?
    first_throttled = next((r.concurrency for r in level_results if r.count_429 > 0), None)

    print(f"  --> baseline p50: {fmt_ms(baseline)} ms")
    if first_throttled is not None:
        print(f"  --> rate limiter first returned 429 at concurrency {first_throttled} "
              f"(this is the cap doing its job before real degradation)")
    if knee is not None:
        print(f"  --> latency/error knee at concurrency ~{knee}")
    else:
        print("  --> no knee reached within the tested levels — raise --levels to push further")

    return {
        "scenario": name,
        "baseline_p50_ms": None if baseline != baseline else round(baseline * 1000, 1),
        "first_throttled_concurrency": first_throttled,
        "knee_concurrency": knee,
        "levels": [
            {
                "concurrency": r.concurrency,
                "rps": round(r.rps, 1),
                "p50_ms": None if r.pct(50) != r.pct(50) else round(r.pct(50) * 1000, 1),
                "p95_ms": None if r.pct(95) != r.pct(95) else round(r.pct(95) * 1000, 1),
                "p99_ms": None if r.pct(99) != r.pct(99) else round(r.pct(99) * 1000, 1),
                "count_2xx": r.count_2xx,
                "count_429": r.count_429,
                "count_5xx": r.count_5xx,
                "count_fail": r.count_fail,
                "notes": r.notes,
            }
            for r in level_results
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Capacity probe for an Imperial 2030 deployment you own.")
    parser.add_argument("--base", required=True, help="Base URL, e.g. http://51.83.187.156")
    parser.add_argument("--scenario", default="anon",
                        choices=list(SCENARIOS) + ["all"], help="Which endpoint to probe")
    parser.add_argument("--levels", default="5,10,20,40",
                        help="Comma-separated concurrency levels to ramp through")
    parser.add_argument("--seconds", type=float, default=8.0, help="Seconds at each level")
    parser.add_argument("--timeout", type=float, default=20.0, help="Per-request timeout (s)")
    parser.add_argument("--game-id", default=None,
                        help="Finished game id for 'detail'/'replay' (auto-discovered if omitted)")
    parser.add_argument("--json", default=None, help="Write a JSON report to this path")
    parser.add_argument("--i-own-this", action="store_true",
                        help="Required. Confirms you own or are authorised to load-test --base.")
    args = parser.parse_args()

    if not args.i_own_this:
        print("Refusing to start without --i-own-this.\n"
              "This sends sustained load to --base. Only run it against a server you own or are\n"
              "explicitly authorised to test.", file=sys.stderr)
        return 2

    try:
        levels = [int(x) for x in args.levels.split(",") if x.strip()]
    except ValueError:
        print("--levels must be comma-separated integers, e.g. 5,10,20,40", file=sys.stderr)
        return 2
    if not levels:
        print("--levels is empty", file=sys.stderr)
        return 2

    scenarios = list(SCENARIOS) if args.scenario == "all" else [args.scenario]

    game_id = args.game_id
    if any(s in NEEDS_GAME_ID for s in scenarios) and game_id is None:
        print("Discovering a finished game id ...")
        game_id = discover_finished_game(args.base, args.timeout)
        if game_id is None:
            print("Could not find a finished game — pass --game-id explicitly, or the server has none.",
                  file=sys.stderr)
            # Still allow scenarios that don't need one.
            scenarios = [s for s in scenarios if s not in NEEDS_GAME_ID]
            if not scenarios:
                return 2
        else:
            print(f"Using finished game: {game_id}")

    print(f"\nTarget: {args.base}")
    print(f"Levels: {levels}   seconds/level: {args.seconds}   timeout: {args.timeout}s")
    print("Reading 2xx=served  429=rate-limited(healthy)  5xx=server-error  fail=timeout/refused\n")

    reports = []
    try:
        for name in scenarios:
            reports.append(run_scenario(name, args.base, game_id, levels, args.seconds, args.timeout))
    except KeyboardInterrupt:
        print("\nInterrupted — reporting what completed.", file=sys.stderr)

    print("\n=== summary ===")
    for r in reports:
        print(f"  {r['scenario']:<7} baseline p50 {str(r['baseline_p50_ms']):>7} ms | "
              f"first 429 @ conc {r['first_throttled_concurrency']} | "
              f"knee @ conc {r['knee_concurrency']}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"target": args.base, "levels": levels, "seconds": args.seconds,
                       "scenarios": reports}, fh, indent=2)
        print(f"\nJSON report written to {args.json}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
