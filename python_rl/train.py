import numpy as np
from sb3_contrib import MaskablePPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor
from imperial_env import ImperialEnv

from stable_baselines3.common.vec_env import DummyVecEnv, SubprocVecEnv, VecNormalize
from stable_baselines3.common.callbacks import BaseCallback, CallbackList


def make_env(bot_type, opponents_list):
    """Factory for a single (Monitor-wrapped) env instance, for use with Subproc/DummyVecEnv.
    Each instance opens its own TCP connection to the training server (port 5295), which handles
    concurrent sessions independently, so these can run as genuinely parallel OS processes."""
    def _init():
        return Monitor(ImperialEnv(bot_type=bot_type, opponents=opponents_list))
    return _init

def linear_schedule(initial_value, final_value=1e-5):
    """Linear decay from initial_value to final_value over training."""
    def func(progress_remaining):
        # progress_remaining goes from 1.0 -> 0.0
        return final_value + progress_remaining * (initial_value - final_value)
    return func

class RunRelativeSchedule:
    """Progress within THIS run, 0.0 -> 1.0.

    Every schedule below used to divide the *cumulative* num_timesteps by a fixed constant. With
    reset_num_timesteps=False that fraction never restarts, so once cumulative steps pass the constant
    every schedule is pinned at its final value forever. RL-3 reached 68.5M against a TOTAL_TIMESTEPS of
    20M, which means ent_coef had been clamped to its 0.015 floor since step 20M - 48 million steps with
    the exploration term at its minimum. A policy cannot unlearn an aversion it never explores away from,
    so this is a prerequisite for the factory-aversion work rather than a tidy-up.

    Anchoring on num_timesteps at training start makes each resumed run get its own full schedule, which
    is what "resume and keep training" is supposed to mean.
    """

    def __init__(self, run_timesteps):
        self.run_timesteps = max(1, run_timesteps)
        self._start = None

    def start(self, num_timesteps):
        self._start = num_timesteps

    def progress(self, num_timesteps):
        if self._start is None:
            return 0.0
        return min(1.0, max(0.0, (num_timesteps - self._start) / self.run_timesteps))


class EntCoefScheduleCallback(BaseCallback):
    """Linearly decays the entropy coefficient over the current run (see RunRelativeSchedule)."""

    def __init__(self, initial_ent_coef, final_ent_coef, run_timesteps, verbose=0):
        super().__init__(verbose)
        self.initial_ent_coef = initial_ent_coef
        self.final_ent_coef = final_ent_coef
        self.schedule = RunRelativeSchedule(run_timesteps)

    def _on_training_start(self) -> None:
        self.schedule.start(self.num_timesteps)

    def _on_step(self) -> bool:
        progress = self.schedule.progress(self.num_timesteps)
        new_ent_coef = self.initial_ent_coef - progress * (self.initial_ent_coef - self.final_ent_coef)
        self.model.ent_coef = new_ent_coef
        self.logger.record("train/current_ent_coef", new_ent_coef)
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


class CurriculumCallback(BaseCallback):
    """Pushes the reward-shaping curriculum into the envs, which forward it to the C# server on reset."""

    def __init__(self, run_timesteps, verbose=0):
        super().__init__(verbose)
        self.schedule = RunRelativeSchedule(run_timesteps)
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
        shaping, factory = self._scales(self.schedule.progress(self.num_timesteps))
        self.training_env.env_method("set_curriculum", shaping, factory)
        self.logger.record("curriculum/shaping_scale", shaping)
        self.logger.record("curriculum/factory_penalty_scale", factory)
        if self.verbose > 0:
            print(f"[curriculum] step {self.num_timesteps:,}: "
                  f"shaping_scale={shaping:.2f} factory_penalty_scale={factory:.2f}")

    def _on_training_start(self) -> None:
        self.schedule.start(self.num_timesteps)
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
    parser.add_argument("--bot-type", type=str, default="RL", help="The name of the bot to train (e.g. RL, RL-2).")
    parser.add_argument("--opponents", type=str, help="Comma separated list of opponents to train against (e.g. Random,Default,RL).")
    parser.add_argument("--n-envs", type=int, default=4, help="Number of parallel training environments (separate OS processes, each with its own TCP session to the C# server). 1 falls back to a single in-process env.")
    args = parser.parse_args()

    opponents_list = args.opponents.split(",") if args.opponents else []

    # Total experience collected per PPO update, independent of how many parallel envs collect it (SB3's
    # n_steps is PER env, so total buffer = n_steps * n_envs). Dividing by n_envs here keeps the update
    # cadence/batch composition the same as the original single-env tuning — parallelizing only changes how
    # fast that same amount of experience is collected in wall-clock time, not the PPO hyperparameters.
    TOTAL_N_STEPS = 8192
    n_steps_per_env = max(1, TOTAL_N_STEPS // args.n_envs)

    env_fns = [make_env(args.bot_type, opponents_list) for _ in range(args.n_envs)]
    # SubprocVecEnv runs each env in its own OS process for genuine parallelism (Python's GIL means
    # DummyVecEnv would just interleave them on one core). Each worker opens its own socket to the training
    # server, which handles concurrent sessions independently (see the ConcurrentDictionary session store).
    vec_env = SubprocVecEnv(env_fns) if args.n_envs > 1 else DummyVecEnv(env_fns)

    MODEL_BASENAME = args.bot_type
    MODEL_PATH = f"{MODEL_BASENAME}.zip"
    VEC_NORM_PATH = "vec_normalize.pkl"
    BEST_VEC_NORM_PATH = "vec_normalize_best.pkl"
    BEST_REWARD_PATH = "best_reward.txt"

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

    if not args.reset and os.path.exists(MODEL_PATH) and os.path.exists(VEC_NORM_PATH):
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
            "learning_rate": linear_schedule(6e-5, 2e-5),
            "n_steps": n_steps_per_env,
            "batch_size": 512,
            "clip_range": 0.2,
            "ent_coef": INITIAL_ENT_COEF,
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
    else:
        print("No existing model found. Initializing new MaskablePPO Model...")
        # CRITICAL: norm_obs=False because state is now manually normalized in C#
        vec_env = VecNormalize(vec_env, norm_obs=False, norm_reward=True, clip_obs=10.0)

        policy_kwargs = dict(
            net_arch=dict(
                pi=[1024, 512],
                vf=[1024, 512, 256],
            )
        )

        model = MaskablePPO(
            "MlpPolicy",
            vec_env,
            policy_kwargs=policy_kwargs,
            learning_rate=linear_schedule(6e-5, 2e-5),
            n_steps=n_steps_per_env,
            batch_size=512,
            clip_range=0.2,
            ent_coef=INITIAL_ENT_COEF,  # See comment above: bumped up for the larger, more heterogeneous action space
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
        def __init__(self, save_freq, save_path, reset=False, verbose=1):
            super().__init__(verbose)
            self.save_freq = save_freq
            self.save_path = save_path
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
                mean_reward = np.mean([ep_info["r"] for ep_info in self.model.ep_info_buffer])
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
    # Both the LR schedule above and EntCoefScheduleCallback below decay linearly as a fraction of this
    # constant (num_timesteps / TOTAL_TIMESTEPS), and that fraction is CUMULATIVE across resumed runs
    # (reset_num_timesteps=False). RL-3 hit ~84% of the original 10M here, meaning both LR and entropy
    # were nearly fully decayed right around when RL-2 was added as an opponent (see tb_logs: ep_rew_mean
    # regressed hard at step ~3M and never reclaimed its pre-regression peak over the following 5M+ steps).
    # That's the schedules starving the agent of both step-size and exploration exactly when the harder
    # opponent needed more of both. Raised to give real runway for both schedules to operate at
    # meaningfully higher values again, rather than continuing to taper toward an already-reached floor.
    TOTAL_TIMESTEPS = 20_000_000

    save_callback = SaveOnStepCallback(save_freq=5000, save_path="./", reset=args.reset)
    # run_timesteps, not a cumulative total: TOTAL_TIMESTEPS is this run's budget (model.learn adds it to
    # num_timesteps internally when reset_num_timesteps=False), so both schedules span exactly this run.
    ent_coef_callback = EntCoefScheduleCallback(initial_ent_coef=INITIAL_ENT_COEF, final_ent_coef=FINAL_ENT_COEF, run_timesteps=TOTAL_TIMESTEPS)
    curriculum_callback = CurriculumCallback(run_timesteps=TOTAL_TIMESTEPS)

    callback = CallbackList([save_callback, ent_coef_callback, curriculum_callback])
    
    model.learn(total_timesteps=TOTAL_TIMESTEPS, reset_num_timesteps=False, callback=callback, tb_log_name=args.bot_type)

    print("Saving Final Model and VecNormalize statistics...")
    model.save(MODEL_BASENAME)
    vec_env.save(VEC_NORM_PATH)
    print("Training Complete!")
