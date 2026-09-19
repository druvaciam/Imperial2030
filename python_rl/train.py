import numpy as np
from sb3_contrib import MaskablePPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor
from imperial_env import ImperialEnv

from stable_baselines3.common.vec_env import DummyVecEnv, SubprocVecEnv, VecNormalize
from stable_baselines3.common.callbacks import BaseCallback, CallbackList
from opponent_curriculum import OpponentCurriculum
from training_schedules import (
    MutableValueSchedule,
    SchedulePhase,
    TrainingScheduleState,
    infer_legacy_phase,
    infer_legacy_learning_rate_phase,
    load_schedule_state,
    read_checkpoint_schedule_metadata,
    save_schedule_state,
)


def make_env(bot_type, opponents_list):
    """Factory for a single (Monitor-wrapped) env instance, for use with Subproc/DummyVecEnv.
    Each instance opens its own TCP connection to the training server (port 5295), which handles
    concurrent sessions independently, so these can run as genuinely parallel OS processes."""
    def _init():
        return Monitor(ImperialEnv(bot_type=bot_type, opponents=opponents_list))
    return _init

class CumulativeSchedule:
    """Progress across the agent's ENTIRE training history, 0.0 -> 1.0, and never backwards.

    Exploration schedules use an explicitly persisted SchedulePhase, which can be restarted for an
    intentional fine-tune. Reward shaping is instead a property of what the agent has already LEARNED:
    holding the wasted-Factory penalty off early only makes sense once, at the very start of the agent's
    life, so it can discover what a factory pays back before being punished for reaching for one.

    Anchoring the curriculum per-run instead meant every restart switched that penalty off again for
    another 3M steps. RL-4's training was restarted six times, and its tb_logs show the result:

        run 1 (   65k steps)  factory_penalty_scale max 0.000
        run 2 (13.7M steps)                         max 1.000
        run 3 (   24k steps)  --reset               max 0.000
        run 4 (17.0M steps)                         max 1.000
        run 5 ( 7.4M steps)                         max 0.874
        run 6 (  966k steps)                        max 0.000   <- the final million steps

    RL-4_best.zip, the checkpoint RL-4.onnx was exported from, sits at 22.35M cumulative but only 26% of
    the way through run 5 - so it was saved with the wasted-Factory penalty running at 45% strength.
    That is the reward term that is supposed to teach "do not move to Factory with no money or nothing
    left to build", which is exactly the behaviour that came back.

    Clamped at 1.0, so once the curriculum has finished it stays finished no matter how often training
    is resumed.
    """

    def __init__(self, total_timesteps):
        self.total_timesteps = max(1, total_timesteps)

    def progress(self, num_timesteps):
        return min(1.0, max(0.0, num_timesteps / self.total_timesteps))


class HyperparameterScheduleCallback(BaseCallback):
    """Advances learning rate and entropy from a phase tied to cumulative model timesteps."""

    def __init__(
        self,
        schedule_state,
        initial_learning_rate,
        final_learning_rate,
        initial_ent_coef,
        final_ent_coef,
        verbose=1,
    ):
        super().__init__(verbose)
        self.schedule_state = schedule_state
        self.initial_learning_rate = initial_learning_rate
        self.final_learning_rate = final_learning_rate
        self.initial_ent_coef = initial_ent_coef
        self.final_ent_coef = final_ent_coef
        self.learning_rate_schedule = MutableValueSchedule(initial_learning_rate)

    def _values(self):
        learning_rate = self.schedule_state.learning_rate_phase.value(
            self.num_timesteps,
            self.initial_learning_rate,
            self.final_learning_rate,
        )
        ent_coef = self.schedule_state.entropy_phase.value(
            self.num_timesteps,
            self.initial_ent_coef,
            self.final_ent_coef,
        )
        return learning_rate, ent_coef

    def _apply(self, update_optimizer=False):
        learning_rate, ent_coef = self._values()
        self.learning_rate_schedule.value = learning_rate
        self.model.learning_rate = learning_rate
        self.model.ent_coef = ent_coef
        if update_optimizer:
            for parameter_group in self.model.policy.optimizer.param_groups:
                parameter_group["lr"] = learning_rate
        self.logger.record("train/current_learning_rate", learning_rate)
        self.logger.record("train/current_ent_coef", ent_coef)
        return learning_rate, ent_coef

    def _on_training_start(self) -> None:
        # PPO asks lr_schedule for a value immediately before each optimization pass. Keeping this
        # mutable callable on the model avoids relying on SB3's process-relative progress_remaining.
        self.model.lr_schedule = self.learning_rate_schedule
        learning_rate, ent_coef = self._apply(update_optimizer=True)
        if self.verbose > 0:
            lr_progress = self.schedule_state.learning_rate_phase.progress(self.num_timesteps)
            entropy_progress = self.schedule_state.entropy_phase.progress(self.num_timesteps)
            print(f"[schedule] step {self.num_timesteps:,}: lr phase {lr_progress:.1%}, "
                  f"entropy phase {entropy_progress:.1%}, learning_rate={learning_rate:.8f}, "
                  f"ent_coef={ent_coef:.6f}")

    def _on_step(self) -> bool:
        self._apply()
        return True


# --- Reward curriculum -------------------------------------------------------------------------
# Both scales are sent to the C# training server on each episode reset and multiply its shaping terms.

# The wasted-Factory and avoidable-skip penalties are held OFF for this fraction of the run, then ramped
# back to full by FACTORY_PENALTY_FULL_AT. A nation normally gets only two builds per game - four home
# cities holding one factory each (Imperial-2030-Rules.pdf p.7), two already built at setup (p.4) - so
# after the second build the Factory slot is usually dead for the rest of the episode and every further
# landing on it is penalised. (Not always: three armies can destroy a factory (p.11) and the freed city
# can be rebuilt, but that is rare.) An agent that has not yet seen the payoff learns to avoid the slot
# entirely, which is what RL-3 did (0.30 factories built per nation-stint vs 1.54 for the heuristic bots).
# Letting it find the payoff first, then restoring the penalty, teaches "build when you can" instead of
# "never go there".
FACTORY_PENALTY_OFF_UNTIL = 0.15
FACTORY_PENALTY_FULL_AT = 0.40

# Shaping magnitude decays over the back half of the run so the terminal win/loss signal ends up dominant.
# The shaping terms are dense, immediate and large (up to -80) while the terminal bonus is +/-100 arriving
# once per ~61-step episode; an agent optimising the sum learns "never get penalised" ahead of "win".
# Floored rather than driven to zero - the shaping still encodes genuinely correct play, it just should
# not outweigh the objective by the end.
SHAPING_DECAY_START = 0.50
SHAPING_SCALE_FINAL = 0.30

# env_method is an IPC round trip per worker under SubprocVecEnv, so pushing every step would cost more
# than the training it is shaping. The schedules move slowly enough that this granularity is invisible.
CURRICULUM_PUSH_EVERY = 10_000

# A cumulative milestone shared by reward and opponent curricula. It is deliberately independent of a
# single model.learn call: ordinary stop/start training must neither restore easy rewards nor easy bots.
# At 10M: Friendly joins at 2M, Aggressive 4M, Greedy 6M, the earlier RL generations from 7.5M to 9M;
# the wasted-Factory penalty ramps in from 1.5M to 4M.
CURRICULUM_TIMESTEPS = 10_000_000


class CurriculumCallback(BaseCallback):
    """Pushes reward and opponent curricula into each environment for its next episode."""

    def __init__(self, total_timesteps, opponent_curriculum=None, fixed_opponents=None, verbose=1):
        super().__init__(verbose)
        # Cumulative, NOT run-relative - see CumulativeSchedule for why they differ.
        self.schedule = CumulativeSchedule(total_timesteps)
        self.opponent_curriculum = opponent_curriculum
        self.fixed_opponents = tuple(fixed_opponents) if fixed_opponents is not None else None
        self._last_push = None

    def _scales(self, progress):
        if progress <= FACTORY_PENALTY_OFF_UNTIL:
            factory = 0.0
        elif progress >= FACTORY_PENALTY_FULL_AT:
            factory = 1.0
        else:
            span = FACTORY_PENALTY_FULL_AT - FACTORY_PENALTY_OFF_UNTIL
            factory = (progress - FACTORY_PENALTY_OFF_UNTIL) / span

        if progress <= SHAPING_DECAY_START:
            shaping = 1.0
        else:
            span = 1.0 - SHAPING_DECAY_START
            t = (progress - SHAPING_DECAY_START) / span
            shaping = 1.0 - t * (1.0 - SHAPING_SCALE_FINAL)

        return shaping, factory

    def _push(self):
        progress = self.schedule.progress(self.num_timesteps)
        shaping, factory = self._scales(progress)
        if self.fixed_opponents is not None:
            opponent_stage = -1
            opponents = self.fixed_opponents
        else:
            opponent_stage, opponents = self.opponent_curriculum.stage_at(progress)

        self.training_env.env_method("set_curriculum", shaping, factory, list(opponents))
        self.logger.record("curriculum/shaping_scale", shaping)
        self.logger.record("curriculum/factory_penalty_scale", factory)
        self.logger.record("curriculum/opponent_stage", opponent_stage)
        self.logger.record("curriculum/opponent_count", len(opponents))
        if self.verbose > 0:
            print(f"[curriculum] step {self.num_timesteps:,}: "
                  f"shaping_scale={shaping:.2f} factory_penalty_scale={factory:.2f} "
                  f"opponents={','.join(opponents)}")

    def _on_training_start(self) -> None:
        progress = self.schedule.progress(self.num_timesteps)
        print(f"[curriculum] resuming at {self.num_timesteps:,} cumulative steps "
              f"({progress:.0%} of the curriculum budget); scales {self._scales(progress)}")
        self._push()
        self._last_push = self.num_timesteps

    def _on_step(self) -> bool:
        if self._last_push is None or self.num_timesteps - self._last_push >= CURRICULUM_PUSH_EVERY:
            self._push()
            self._last_push = self.num_timesteps
        return True


if __name__ == "__main__":
    import os
    import argparse

    parser = argparse.ArgumentParser(description="Train the Imperial 2030 RL Bot.")
    parser.add_argument("--reset", action="store_true", help="Start training from scratch, ignoring any existing saved model.")
    parser.add_argument(
        "--restart-schedules",
        action="store_true",
        help="Start a new learning-rate/entropy phase at the loaded checkpoint (intentional fine-tuning only).",
    )
    parser.add_argument("--bot-type", type=str, default="RL", help="The name of the bot to train (e.g. RL, RL-2).")
    parser.add_argument(
        "--opponents",
        type=str,
        help="Fixed comma-separated opponent pool; overrides the automatic stage-based curriculum.",
    )
    parser.add_argument("--n-envs", type=int, default=4, help="Number of parallel training environments (separate OS processes, each with its own TCP session to the C# server). 1 falls back to a single in-process env.")
    args = parser.parse_args()

    fixed_opponents = (tuple(opponent.strip() for opponent in args.opponents.split(",") if opponent.strip())
                       if args.opponents else None)

    MODEL_BASENAME = args.bot_type
    MODEL_PATH = f"{MODEL_BASENAME}.zip"
    VEC_NORM_PATH = "vec_normalize.pkl"
    BEST_VEC_NORM_PATH = "vec_normalize_best.pkl"
    BEST_REWARD_PATH = "best_reward.txt"
    SCHEDULE_STATE_PATH = f"{MODEL_BASENAME}_schedule_state.json"
    is_resume = not args.reset and os.path.exists(MODEL_PATH) and os.path.exists(VEC_NORM_PATH)

    if args.restart_schedules and not is_resume:
        parser.error("--restart-schedules requires an existing model and VecNormalize checkpoint, without --reset")

    # Total experience collected per PPO update, independent of how many parallel envs collect it (SB3's
    # n_steps is PER env, so total buffer = n_steps * n_envs). Dividing by n_envs here keeps the update
    # cadence/batch composition the same as the original single-env tuning — parallelizing only changes how
    # fast that same amount of experience is collected in wall-clock time, not the PPO hyperparameters.
    TOTAL_N_STEPS = 8192
    n_steps_per_env = max(1, TOTAL_N_STEPS // args.n_envs)

    # Optional: TensorBoard logging for watching ep_rew_mean etc. trend over time. Degrades to plain console
    # logging (instead of hard-crashing training) if the `tensorboard` package isn't installed — install it
    # with `pip install tensorboard` (also listed in requirements.txt) to actually get the logs.
    try:
        import tensorboard  # noqa: F401
        TENSORBOARD_LOG_DIR = "./tb_logs"
    except ImportError:
        print("WARNING: 'tensorboard' package not installed — skipping TensorBoard logging (pip install tensorboard to enable). Falling back to console-only logging.")
        TENSORBOARD_LOG_DIR = None

    # Exploration: 0.05 -> 0.015. Bumped up from the original 0.03 -> 0.005 (tuned back when the action space
    # was 64) now that it's 205 and includes several newer decision types (Import placement, Factory
    # build/destroy) that occur far less often per game than Rondel moves, so they need sustained exploration
    # pressure for longer to collect enough samples, rather than collapsing onto the well-understood actions.
    INITIAL_ENT_COEF = 0.05
    FINAL_ENT_COEF = 0.015
    INITIAL_LEARNING_RATE = 6e-5
    FINAL_LEARNING_RATE = 2e-5
    TOTAL_TIMESTEPS = 10_000_000

    schedule_state_needs_write = False
    if is_resume:
        try:
            checkpoint_metadata = read_checkpoint_schedule_metadata(MODEL_PATH)
        except (OSError, KeyError, TypeError, ValueError) as error:
            raise RuntimeError(f"Could not read schedule metadata from {MODEL_PATH}: {error}") from error
        saved_model_timesteps = checkpoint_metadata.model_timesteps

        if args.restart_schedules:
            schedule_state = TrainingScheduleState(
                learning_rate_phase=SchedulePhase(saved_model_timesteps, TOTAL_TIMESTEPS),
                entropy_phase=SchedulePhase(saved_model_timesteps, TOTAL_TIMESTEPS),
            )
            schedule_state_needs_write = True
            print(f"Restarting learning-rate/entropy schedules at checkpoint step "
                  f"{saved_model_timesteps:,} (--restart-schedules).")
        else:
            schedule_state = load_schedule_state(SCHEDULE_STATE_PATH, saved_model_timesteps)
            if schedule_state is not None:
                lr_progress = schedule_state.learning_rate_phase.progress(saved_model_timesteps)
                entropy_progress = schedule_state.entropy_phase.progress(saved_model_timesteps)
                print(f"Continuing persisted schedules: LR {lr_progress:.1%}, "
                      f"entropy {entropy_progress:.1%} complete.")
            else:
                learning_rate_phase = infer_legacy_learning_rate_phase(
                    checkpoint_metadata,
                    default_duration_timesteps=TOTAL_TIMESTEPS,
                    initial_value=INITIAL_LEARNING_RATE,
                    final_value=FINAL_LEARNING_RATE,
                )
                saved_ent_coef = checkpoint_metadata.saved_entropy
                if (saved_ent_coef is not None
                        and min(INITIAL_ENT_COEF, FINAL_ENT_COEF) <= saved_ent_coef
                        <= max(INITIAL_ENT_COEF, FINAL_ENT_COEF)):
                    entropy_phase = infer_legacy_phase(
                        model_timesteps=saved_model_timesteps,
                        duration_timesteps=TOTAL_TIMESTEPS,
                        saved_value=saved_ent_coef,
                        initial_value=INITIAL_ENT_COEF,
                        final_value=FINAL_ENT_COEF,
                    )
                else:
                    entropy_phase = SchedulePhase(saved_model_timesteps, TOTAL_TIMESTEPS)

                schedule_state = TrainingScheduleState(
                    learning_rate_phase=learning_rate_phase,
                    entropy_phase=entropy_phase,
                )
                schedule_state_needs_write = True
                migrated_learning_rate = learning_rate_phase.value(
                    saved_model_timesteps,
                    INITIAL_LEARNING_RATE,
                    FINAL_LEARNING_RATE,
                )
                migrated_ent_coef = entropy_phase.value(
                    saved_model_timesteps,
                    INITIAL_ENT_COEF,
                    FINAL_ENT_COEF,
                )
                print(f"Migrating independent legacy schedules: learning_rate={migrated_learning_rate:.8f} "
                      f"({learning_rate_phase.progress(saved_model_timesteps):.1%}), "
                      f"ent_coef={migrated_ent_coef:.6f} "
                      f"({entropy_phase.progress(saved_model_timesteps):.1%}).")
    else:
        saved_model_timesteps = 0
        schedule_state = TrainingScheduleState(
            learning_rate_phase=SchedulePhase(0, TOTAL_TIMESTEPS),
            entropy_phase=SchedulePhase(0, TOTAL_TIMESTEPS),
        )

    current_learning_rate = schedule_state.learning_rate_phase.value(
        saved_model_timesteps,
        INITIAL_LEARNING_RATE,
        FINAL_LEARNING_RATE,
    )
    current_ent_coef = schedule_state.entropy_phase.value(
        saved_model_timesteps,
        INITIAL_ENT_COEF,
        FINAL_ENT_COEF,
    )

    opponent_curriculum = None if fixed_opponents is not None else OpponentCurriculum(args.bot_type)
    curriculum_progress = CumulativeSchedule(CURRICULUM_TIMESTEPS).progress(saved_model_timesteps)
    if fixed_opponents is not None:
        initial_opponents = fixed_opponents
        print(f"Using fixed --opponents override: {','.join(initial_opponents)}")
    else:
        opponent_stage, initial_opponents = opponent_curriculum.stage_at(curriculum_progress)
        print(f"[curriculum] initial opponent stage {opponent_stage} at {curriculum_progress:.0%}: "
              f"{','.join(initial_opponents)}")

    env_fns = [make_env(args.bot_type, list(initial_opponents)) for _ in range(args.n_envs)]
    # SubprocVecEnv runs each env in its own OS process for genuine parallelism (Python's GIL means
    # DummyVecEnv would just interleave them on one core). Each worker opens its own socket to the training
    # server, which handles concurrent sessions independently (see the ConcurrentDictionary session store).
    vec_env = SubprocVecEnv(env_fns) if args.n_envs > 1 else DummyVecEnv(env_fns)

    if is_resume:
        print("Found existing model, resuming training...")
        vec_env = VecNormalize.load(VEC_NORM_PATH, vec_env)
        # We must disable training mode when not training, but here we ARE training
        vec_env.training = True
        # A brief experiment at linear_schedule(1.5e-4, 2e-5) (~3.6x this) caused a sustained ep_rew_mean
        # regression starting right at the resume step (tb_logs: -73 plateau -> steady decline to -141 over
        # the next 1.2M steps, with approx_kl/clip_fraction both jumping ~2-3x at the same point) — too large
        # an update for an already-partially-converged policy. Back to the last value that was stable
        # (plateaued, not regressing).
        custom_objects = {
            "learning_rate": current_learning_rate,
            "n_steps": n_steps_per_env,
            "batch_size": 512,
            "clip_range": 0.2,
            "ent_coef": current_ent_coef,
            # MEASURED, do not "fix": episodes are ~61 agent steps (rollout/ep_len_mean over RL-3's
            # 8,646 logged samples: min 44, max 70, mean 58). gamma=0.995 has a 138-step half-life, so the
            # terminal win/loss reward still arrives with 73% of its value intact. The horizon is NOT
            # shorter than the game, and annealing gamma upward (0.999 would take that 73% to 94%) is not
            # the lever - the shaping-vs-terminal imbalance handled by CurriculumCallback is.
            "gamma": 0.995,
            "n_epochs": 6,
            "max_grad_norm": 0.5,
            "tensorboard_log": TENSORBOARD_LOG_DIR,
        }
        model = MaskablePPO.load(MODEL_PATH, env=vec_env, custom_objects=custom_objects, verbose=1)
        if schedule_state_needs_write:
            # Commit an explicit restart or deterministic legacy migration only after the model loads.
            save_schedule_state(SCHEDULE_STATE_PATH, schedule_state, model.num_timesteps)
    else:
        print("No existing model found. Initializing new MaskablePPO Model...")
        # CRITICAL: norm_obs=False because state is now manually normalized in C#
        vec_env = VecNormalize(vec_env, norm_obs=False, norm_reward=True, clip_obs=10.0)

        policy_kwargs = dict(
            net_arch=dict(
                pi=[1024, 1024],
                vf=[1024, 1024],
            )
        )

        model = MaskablePPO(
            "MlpPolicy",
            vec_env,
            policy_kwargs=policy_kwargs,
            learning_rate=current_learning_rate,
            n_steps=n_steps_per_env,
            batch_size=512,
            clip_range=0.2,
            ent_coef=current_ent_coef,  # See comment above: bumped up for the larger, more heterogeneous action space
            # MEASURED, do not "fix": episodes are ~61 agent steps (rollout/ep_len_mean over RL-3's
            # 8,646 logged samples: min 44, max 70, mean 58). gamma=0.995 has a 138-step half-life, so the
            # terminal win/loss reward still arrives with 73% of its value intact. The horizon is NOT
            # shorter than the game, and annealing gamma upward (0.999 would take that 73% to 94%) is not
            # the lever - the shaping-vs-terminal imbalance handled by CurriculumCallback is.
            gamma=0.995,
            n_epochs=6,
            max_grad_norm=0.5,
            tensorboard_log=TENSORBOARD_LOG_DIR,
            verbose=1
        )

    class SaveOnStepCallback(BaseCallback):
        def __init__(self, save_freq, save_path, schedule_state, schedule_state_path, reset=False, verbose=1):
            super().__init__(verbose)
            self.save_freq = save_freq
            self.save_path = save_path
            self.schedule_state = schedule_state
            self.schedule_state_path = schedule_state_path
            self.best_mean_reward = -np.inf
            self.best_reward_file = os.path.join(save_path, BEST_REWARD_PATH)
            
            if reset and os.path.exists(self.best_reward_file):
                os.remove(self.best_reward_file)
                print("Resetting best reward tracking...")
            elif os.path.exists(self.best_reward_file):
                try:
                    with open(self.best_reward_file, "r") as f:
                        self.best_mean_reward = float(f.read().strip())
                    if self.verbose > 0:
                        print(f"Loaded previous best mean reward: {self.best_mean_reward:.2f}")
                except Exception as e:
                    print(f"Could not load best reward file: {e}")

        def _init_callback(self):
            os.makedirs(self.save_path, exist_ok=True)

        def _on_step(self):
            if self.n_calls % self.save_freq == 0:
                # Save the latest model (for resuming training)
                self.model.save(os.path.join(self.save_path, MODEL_BASENAME))
                self.training_env.save(os.path.join(self.save_path, VEC_NORM_PATH))
                save_schedule_state(
                    os.path.join(self.save_path, self.schedule_state_path),
                    self.schedule_state,
                    self.num_timesteps,
                )
                mean_reward = (np.mean([ep_info["r"] for ep_info in self.model.ep_info_buffer])
                               if len(self.model.ep_info_buffer) > 0 else float("nan"))
                if self.verbose > 0:
                    print(f"Saved latest checkpoint at step {self.num_timesteps}, mean reward {mean_reward:.2f}")

                # Check if we have a new best model
                if len(self.model.ep_info_buffer) > 0:
                    if mean_reward > self.best_mean_reward:
                        self.best_mean_reward = mean_reward
                        if self.verbose > 0:
                            print(f"*** New best mean reward: {mean_reward:.2f}! Saving best model... ***")
                        self.model.save(os.path.join(self.save_path, f"{MODEL_BASENAME}_best"))
                        self.training_env.save(os.path.join(self.save_path, BEST_VEC_NORM_PATH))
                        with open(self.best_reward_file, "w") as f:
                            f.write(str(mean_reward))

            return True

    print("Starting Training...")
    # Train for a larger number of timesteps.
    # It will automatically save every 5,000 steps to the current directory
    # Ordinary process restarts continue the persisted phase. Use --restart-schedules only when a new
    # 20M-step fine-tuning phase (and the corresponding jump back to the initial values) is intentional.
    schedule_callback = HyperparameterScheduleCallback(
        schedule_state=schedule_state,
        initial_learning_rate=INITIAL_LEARNING_RATE,
        final_learning_rate=FINAL_LEARNING_RATE,
        initial_ent_coef=INITIAL_ENT_COEF,
        final_ent_coef=FINAL_ENT_COEF,
    )
    save_callback = SaveOnStepCallback(
        save_freq=5000,
        save_path="./",
        schedule_state=schedule_state,
        schedule_state_path=SCHEDULE_STATE_PATH,
        reset=args.reset,
    )
    curriculum_callback = CurriculumCallback(
        total_timesteps=CURRICULUM_TIMESTEPS,
        opponent_curriculum=opponent_curriculum,
        fixed_opponents=fixed_opponents,
    )

    # Apply the schedule before checkpointing so the model value and sidecar describe the same step.
    callback = CallbackList([schedule_callback, curriculum_callback, save_callback])
    
    model.learn(total_timesteps=TOTAL_TIMESTEPS, reset_num_timesteps=False, callback=callback, tb_log_name=args.bot_type)

    print("Saving Final Model and VecNormalize statistics...")
    model.save(MODEL_BASENAME)
    vec_env.save(VEC_NORM_PATH)
    save_schedule_state(SCHEDULE_STATE_PATH, schedule_state, model.num_timesteps)
    print("Training Complete!")
