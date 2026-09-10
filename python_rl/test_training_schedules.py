import json
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

from training_schedules import (
    CheckpointScheduleMetadata,
    MutableValueSchedule,
    SchedulePhase,
    TrainingScheduleState,
    infer_legacy_phase,
    infer_legacy_learning_rate_phase,
    load_schedule_state,
    read_checkpoint_schedule_metadata,
    save_schedule_state,
)


class TrainingScheduleTests(unittest.TestCase):
    def test_ordinary_resume_continues_learning_rate_and_entropy_independently(self):
        state = TrainingScheduleState(
            learning_rate_phase=SchedulePhase(0, 86_480_000),
            entropy_phase=SchedulePhase(66_480_006, 20_000_000),
        )
        saved_model_steps = 69_000_000

        self.assertAlmostEqual(
            2.808510638e-5,
            state.learning_rate_phase.value(saved_model_steps, 6e-5, 2e-5),
        )
        self.assertAlmostEqual(
            0.0455900105,
            state.entropy_phase.value(saved_model_steps, 0.05, 0.015),
        )

    def test_explicit_restart_begins_a_fresh_phase_at_current_model_step(self):
        model_steps = 68_910_000
        state = TrainingScheduleState(
            learning_rate_phase=SchedulePhase(model_steps, 20_000_000),
            entropy_phase=SchedulePhase(model_steps, 20_000_000),
        )

        self.assertEqual(0.0, state.learning_rate_phase.progress(model_steps))
        self.assertEqual(0.0, state.entropy_phase.progress(model_steps))
        self.assertAlmostEqual(6e-5, state.learning_rate_phase.value(model_steps, 6e-5, 2e-5))
        self.assertAlmostEqual(0.05, state.entropy_phase.value(model_steps, 0.05, 0.015))

    def test_completed_phase_remains_at_the_final_value_after_resume(self):
        phase = SchedulePhase(start_timesteps=5_000_000, duration_timesteps=20_000_000)

        self.assertEqual(1.0, phase.progress(40_000_000))
        self.assertAlmostEqual(2e-5, phase.value(40_000_000, 6e-5, 2e-5))
        self.assertAlmostEqual(2e-5, phase.value(60_000_000, 6e-5, 2e-5))

    def test_legacy_checkpoint_phase_is_inferred_from_saved_entropy(self):
        model_steps = 68_910_000
        saved_entropy = 0.0457475105
        phase = infer_legacy_phase(
            model_timesteps=model_steps,
            duration_timesteps=20_000_000,
            saved_value=saved_entropy,
            initial_value=0.05,
            final_value=0.015,
        )

        self.assertAlmostEqual(saved_entropy, phase.value(model_steps, 0.05, 0.015), places=8)
        self.assertAlmostEqual(0.1214997, phase.progress(model_steps), places=6)

    def test_legacy_callable_learning_rate_uses_saved_sb3_total_timestep_target(self):
        metadata = CheckpointScheduleMetadata(
            model_timesteps=69_000_000,
            total_timesteps=86_480_000,
            learn_start_timesteps=66_480_000,
            saved_entropy=0.0455900105,
            saved_learning_rate=None,
        )

        phase = infer_legacy_learning_rate_phase(
            metadata,
            default_duration_timesteps=20_000_000,
            initial_value=6e-5,
            final_value=2e-5,
        )

        self.assertEqual(SchedulePhase(0, 86_480_000), phase)
        self.assertAlmostEqual(2.808510638e-5, phase.value(69_000_000, 6e-5, 2e-5))

    def test_numeric_learning_rate_from_version_one_checkpoint_is_preserved(self):
        metadata = CheckpointScheduleMetadata(
            model_timesteps=71_400_000,
            total_timesteps=89_000_000,
            learn_start_timesteps=69_000_000,
            saved_entropy=0.0413900105,
            saved_learning_rate=5.0160012e-5,
        )

        phase = infer_legacy_learning_rate_phase(
            metadata,
            default_duration_timesteps=20_000_000,
            initial_value=6e-5,
            final_value=2e-5,
        )

        self.assertAlmostEqual(5.0160012e-5, phase.value(71_400_000, 6e-5, 2e-5))
        self.assertEqual(66_480_006, phase.start_timesteps)

    def test_phase_state_round_trips_and_is_rejected_if_it_belongs_to_a_newer_checkpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "RL-4_schedule_state.json"
            state = TrainingScheduleState(
                learning_rate_phase=SchedulePhase(0, 86_480_000),
                entropy_phase=SchedulePhase(60_000_000, 20_000_000),
            )
            save_schedule_state(path, state, checkpoint_timesteps=68_910_000)

            self.assertEqual(state, load_schedule_state(path, model_timesteps=68_910_000))
            self.assertIsNone(load_schedule_state(path, model_timesteps=10_000))

            payload = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(2, payload["version"])
            self.assertEqual(68_910_000, payload["checkpoint_timesteps"])
            self.assertEqual(86_480_000, payload["learning_rate_phase_duration_timesteps"])
            self.assertEqual(60_000_000, payload["entropy_phase_start_timesteps"])

    def test_bot_types_use_independent_state_files(self):
        with tempfile.TemporaryDirectory() as directory:
            rl3_path = Path(directory) / "RL-3_schedule_state.json"
            rl4_path = Path(directory) / "RL-4_schedule_state.json"
            rl3_state = TrainingScheduleState(SchedulePhase(0, 100), SchedulePhase(10, 100))
            rl4_state = TrainingScheduleState(SchedulePhase(0, 200), SchedulePhase(50, 200))

            save_schedule_state(rl3_path, rl3_state, checkpoint_timesteps=80)
            save_schedule_state(rl4_path, rl4_state, checkpoint_timesteps=80)

            self.assertEqual(rl3_state, load_schedule_state(rl3_path, model_timesteps=80))
            self.assertEqual(rl4_state, load_schedule_state(rl4_path, model_timesteps=80))

    def test_version_one_shared_phase_is_rejected_for_independent_checkpoint_migration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "RL-4_schedule_state.json"
            path.write_text(json.dumps({
                "version": 1,
                "checkpoint_timesteps": 71_400_000,
                "phase_start_timesteps": 66_480_006,
                "phase_duration_timesteps": 20_000_000,
            }), encoding="utf-8")

            self.assertIsNone(load_schedule_state(path, model_timesteps=71_400_000))

    def test_checkpoint_metadata_reads_numeric_entropy_without_importing_sb3(self):
        with tempfile.TemporaryDirectory() as directory:
            checkpoint_path = Path(directory) / "RL-4.zip"
            with ZipFile(checkpoint_path, "w") as checkpoint:
                checkpoint.writestr("data", json.dumps({
                    "num_timesteps": 68_910_000,
                    "_total_timesteps": 86_480_000,
                    "_num_timesteps_at_start": 66_480_000,
                    "ent_coef": 0.0457475105,
                    "learning_rate": 2.81e-5,
                }))

            self.assertEqual(
                CheckpointScheduleMetadata(
                    model_timesteps=68_910_000,
                    total_timesteps=86_480_000,
                    learn_start_timesteps=66_480_000,
                    saved_entropy=0.0457475105,
                    saved_learning_rate=2.81e-5,
                ),
                read_checkpoint_schedule_metadata(checkpoint_path),
            )

    def test_mutable_schedule_exposes_the_value_selected_by_the_callback(self):
        schedule = MutableValueSchedule(6e-5)
        self.assertEqual(6e-5, schedule(0.8))
        schedule.value = 2e-5
        self.assertEqual(2e-5, schedule(0.1))


if __name__ == "__main__":
    unittest.main()
