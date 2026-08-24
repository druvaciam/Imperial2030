#!/usr/bin/env python3
"""Mechanical check for .agents/AGENTS.md rule #19 — EF Core `.AsSplitQuery()`.

Any query chain against `Games` that `.Include()`s two or more COLLECTION navigations must call
`.AsSplitQuery()` before its terminal call, or EF Core joins every collection into one statement and
produces a cartesian-product row explosion.

Rule #19 says explicitly not to trust eyeballing a Grep result for this — a truncated or paginated
manual scan can silently miss the worst offenders — hence a script.

Usage:
    python tools/scan_splitquery.py            # scans Server/ and Tests/
    python tools/scan_splitquery.py Server     # scan specific roots

Exits 1 when violations are found, so it can gate CI.
"""
import glob
import os
import re
import sys

# Collection navigations on Game. Reference navigations (Controller, Holder, User) don't multiply rows
# and are exempt from the rule.
COLLECTION_NAVS = {
    "Players", "NationStates", "TerritoryStates", "Bonds", "Units", "Actions", "GameActions",
}

TERMINALS = re.compile(
    r"(FirstOrDefaultAsync|FirstAsync|SingleOrDefaultAsync|SingleAsync|ToListAsync|ToArrayAsync"
    r"|FirstOrDefault\(|First\(|SingleOrDefault\(|Single\(|ToList\(\)|ToArray\(\))"
)

# `context.Games.Add(x)` is not a query. Without this the scan starts a chain there and sweeps forward
# into the NEXT statement's Includes, reporting a violation on a line that has no query at all.
MUTATORS = re.compile(r"\.Games\s*\.\s*(Add|AddRange|Remove|RemoveRange|Attach|Update|Entry)\s*\(")

QUERY_START = re.compile(r"\.Games\b")
INCLUDE = re.compile(r"\.Include\(\s*\w+\s*=>\s*\w+\.(\w+)\)")

SKIP_DIRS = {"obj", "bin", "Migrations", "node_modules"}
MAX_CHAIN_LINES = 40


def source_files(roots):
    for root in roots:
        pattern = os.path.join(root, "**", "*.cs")
        for path in glob.glob(pattern, recursive=True):
            parts = set(path.replace("\\", "/").split("/"))
            if parts & SKIP_DIRS:
                continue
            yield path


def find_violations(roots):
    for path in source_files(roots):
        with open(path, encoding="utf-8", errors="replace") as handle:
            lines = handle.read().split("\n")

        for index, line in enumerate(lines):
            if not QUERY_START.search(line) or MUTATORS.search(line):
                continue

            chain = []
            cursor = index
            while cursor < len(lines) and cursor < index + MAX_CHAIN_LINES:
                chain.append(lines[cursor])
                if TERMINALS.search(lines[cursor]):
                    break
                cursor += 1

            text = "\n".join(chain)
            # An explicit .AsSingleQuery() is a deliberate opt-out, not an oversight - the author has
            # stated the intent, and EF stops warning too. Only an UNSTATED splitting behaviour is a
            # rule #19 violation.
            if ".AsSplitQuery()" in text or ".AsSingleQuery()" in text:
                continue

            collections = [nav for nav in INCLUDE.findall(text) if nav in COLLECTION_NAVS]
            if len(collections) >= 2:
                yield path, index + 1, collections


def main():
    roots = sys.argv[1:] or ["Server", "Tests"]
    violations = list(find_violations(roots))

    for path, line, collections in violations:
        print(f"{path}:{line}  {len(collections)} collection Includes without AsSplitQuery: "
              f"{', '.join(collections)}")

    if violations:
        print(f"\nFAIL: {len(violations)} violation(s) of AGENTS.md rule #19.")
        return 1

    print(f"OK: no rule #19 violations under {', '.join(roots)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
