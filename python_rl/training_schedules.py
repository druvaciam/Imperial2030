"""Persistent schedule phases for PPO training.

This module deliberately depends only on the Python standard library so its behavior can be tested
without importing Stable-Baselines3, PyTorch, Gymnasium, or opening a training-server socket.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
from zipfile import ZipFile


SCHEDULE_STATE_VERSION = 2


@dataclass(frozen=True)
class SchedulePhase:
    """A schedule phase anchored to the model's cumulative timestep counter."""

    start_timesteps: int
    duration_timesteps: int

    def __post_init__(self):
        if self.start_timesteps < 0:
            raise ValueError("Schedule phase start must be non-negative")
        if self.duration_timesteps <= 0:
            raise ValueError("Schedule phase duration must be positive")

    def progress(self, model_timesteps: float) -> float:
        elapsed = model_timesteps - self.start_timesteps
        return min(1.0, max(0.0, elapsed / self.duration_timesteps))

    def value(self, model_timesteps: float, initial_value: float, final_value: float) -> float:
        progress = self.progress(model_timesteps)
        return initial_value + progress * (final_value - initial_value)


@dataclass(frozen=True)
class TrainingScheduleState:
    """Independent persisted phases for PPO's two scheduled hyperparameters."""

    learning_rate_phase: SchedulePhase
    entropy_phase: SchedulePhase


@dataclass(frozen=True)
class CheckpointScheduleMetadata:
    """Schedule-related values stored in a Stable-Baselines3 checkpoint."""

    model_timesteps: int
    total_timesteps: int | None
    learn_start_timesteps: int | None
    saved_entropy: float | None
    saved_learning_rate: float | None


@dataclass
class MutableValueSchedule:
    """A Stable-Baselines3-compatible constant schedule whose value a callback can advance."""

    value: float

    def __call__(self, _progress_remaining: float) -> float:
        return self.value


def _optional_int(payload: dict, key: str) -> int | None:
    value = payload.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return int(value)


def _optional_float(payload: dict, key: str) -> float | None:
    value = payload.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return float(value)


def read_checkpoint_schedule_metadata(path: str | Path) -> CheckpointScheduleMetadata:
    """Read schedule inputs from an SB3 checkpoint without importing Stable-Baselines3."""

    with ZipFile(path) as checkpoint:
        payload = json.loads(checkpoint.read("data"))

    return CheckpointScheduleMetadata(
        model_timesteps=int(payload["num_timesteps"]),
        total_timesteps=_optional_int(payload, "_total_timesteps"),
        learn_start_timesteps=_optional_int(payload, "_num_timesteps_at_start"),
        saved_entropy=_optional_float(payload, "ent_coef"),
        saved_learning_rate=_optional_float(payload, "learning_rate"),
    )


def infer_legacy_phase(
    model_timesteps: int,
    duration_timesteps: int,
    saved_value: float,
    initial_value: float,
    final_value: float,
) -> SchedulePhase:
    """Reconstruct a phase origin from the value saved in a pre-state-file checkpoint."""

    if initial_value == final_value:
        return SchedulePhase(model_timesteps, duration_timesteps)

    progress = (saved_value - initial_value) / (final_value - initial_value)
    progress = min(1.0, max(0.0, progress))
    elapsed = round(progress * duration_timesteps)
    start_timesteps = max(0, model_timesteps - elapsed)
    return SchedulePhase(start_timesteps, duration_timesteps)


def infer_legacy_learning_rate_phase(
    metadata: CheckpointScheduleMetadata,
    default_duration_timesteps: int,
    initial_value: float,
    final_value: float,
) -> SchedulePhase:
    """Recover LR continuity from either a numeric value or SB3's legacy total-step target.

    Old callable checkpoints used ``1 - num_timesteps / _total_timesteps``. That is exactly the same
    linear curve as a phase beginning at zero and ending at ``_total_timesteps``. Checkpoints written
    by schedule-state version 1 instead contain a numeric learning rate, from which their phase can be
    reconstructed directly.
    """

    saved_value = metadata.saved_learning_rate
    lower_bound = min(initial_value, final_value)
    upper_bound = max(initial_value, final_value)
    if saved_value is not None and lower_bound <= saved_value <= upper_bound:
        return infer_legacy_phase(
            metadata.model_timesteps,
            default_duration_timesteps,
            saved_value,
            initial_value,
            final_value,
        )

    if metadata.total_timesteps is not None and metadata.total_timesteps > 0:
        return SchedulePhase(0, metadata.total_timesteps)

    return SchedulePhase(metadata.model_timesteps, default_duration_timesteps)


def load_schedule_state(path: str | Path, model_timesteps: int) -> TrainingScheduleState | None:
    """Load compatible state, or return ``None`` when it cannot belong to this checkpoint."""

    state_path = Path(path)
    try:
        payload = json.loads(state_path.read_text(encoding="utf-8"))
        if payload["version"] != SCHEDULE_STATE_VERSION:
            return None

        checkpoint_timesteps = int(payload["checkpoint_timesteps"])
        state = TrainingScheduleState(
            learning_rate_phase=SchedulePhase(
                start_timesteps=int(payload["learning_rate_phase_start_timesteps"]),
                duration_timesteps=int(payload["learning_rate_phase_duration_timesteps"]),
            ),
            entropy_phase=SchedulePhase(
                start_timesteps=int(payload["entropy_phase_start_timesteps"]),
                duration_timesteps=int(payload["entropy_phase_duration_timesteps"]),
            ),
        )

        # A state newer than the model normally means --reset began a new lineage but did not yet save
        # its first checkpoint. It must not be applied to the older model still present on disk.
        if (checkpoint_timesteps < 0
                or checkpoint_timesteps > model_timesteps
                or state.learning_rate_phase.start_timesteps > model_timesteps
                or state.entropy_phase.start_timesteps > model_timesteps):
            return None
        return state
    except (OSError, ValueError, TypeError, KeyError, json.JSONDecodeError):
        return None


def save_schedule_state(
    path: str | Path,
    state: TrainingScheduleState,
    checkpoint_timesteps: int,
) -> None:
    """Atomically persist schedule metadata alongside a successfully saved model checkpoint."""

    state_path = Path(path)
    state_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "version": SCHEDULE_STATE_VERSION,
        "checkpoint_timesteps": int(checkpoint_timesteps),
        "learning_rate_phase_start_timesteps": state.learning_rate_phase.start_timesteps,
        "learning_rate_phase_duration_timesteps": state.learning_rate_phase.duration_timesteps,
        "entropy_phase_start_timesteps": state.entropy_phase.start_timesteps,
        "entropy_phase_duration_timesteps": state.entropy_phase.duration_timesteps,
    }
    temporary_path = state_path.with_suffix(state_path.suffix + ".tmp")
    temporary_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary_path, state_path)
