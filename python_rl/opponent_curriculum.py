"""Cumulative opponent progression for Imperial 2030 RL training.

The pool grows monotonically: easy heuristic bots stay present while stronger heuristics and older
RL generations are introduced. The model currently being trained and all future generations are
excluded by construction.
"""

from __future__ import annotations

import re


_HEURISTIC_STAGES = (
    (0.00, ("Random", "Default")),
    (0.20, ("Random", "Default", "Friendly")),
    (0.40, ("Random", "Default", "Friendly", "Aggressive")),
    (0.60, ("Random", "Default", "Friendly", "Aggressive", "Greedy")),
)
_RL_START_PROGRESS = 0.75
_RL_FULL_PROGRESS = 0.90


def previous_rl_opponents(bot_type: str) -> tuple[str, ...]:
    """Return canonical earlier RL generations, never the target or a future model.

    ``RL`` is the original generation. Consequently RL-4 may train against RL, RL-2 and RL-3,
    while the original RL model has no earlier learned opponent.
    """

    normalized = bot_type.strip().upper()
    if normalized == "RL":
        return ()

    match = re.fullmatch(r"RL-(\d+)", normalized)
    if match is None:
        return ()

    target_generation = int(match.group(1))
    if target_generation < 2:
        return ()

    return ("RL",) + tuple(f"RL-{generation}" for generation in range(2, target_generation))


class OpponentCurriculum:
    """Selects an opponent pool from normalized cumulative training progress."""

    def __init__(self, bot_type: str):
        stages = list(_HEURISTIC_STAGES)
        pool = list(stages[-1][1])
        earlier_rl = previous_rl_opponents(bot_type)

        if len(earlier_rl) == 1:
            rl_thresholds = (_RL_START_PROGRESS,)
        elif len(earlier_rl) > 1:
            step = (_RL_FULL_PROGRESS - _RL_START_PROGRESS) / (len(earlier_rl) - 1)
            rl_thresholds = tuple(
                _RL_START_PROGRESS + index * step for index in range(len(earlier_rl))
            )
        else:
            rl_thresholds = ()

        for threshold, opponent in zip(rl_thresholds, earlier_rl):
            pool.append(opponent)
            stages.append((threshold, tuple(pool)))

        self._stages = tuple(stages)

    def stage_at(self, progress: float) -> tuple[int, tuple[str, ...]]:
        progress = min(1.0, max(0.0, float(progress)))
        selected_index = 0
        selected_pool = self._stages[0][1]

        for index, (threshold, pool) in enumerate(self._stages):
            if progress < threshold:
                break
            selected_index = index
            selected_pool = pool

        return selected_index, selected_pool

    def opponents_at(self, progress: float) -> tuple[str, ...]:
        return self.stage_at(progress)[1]
