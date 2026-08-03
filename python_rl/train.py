import numpy as np
from sb3_contrib import MaskablePPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor
from imperial_env import ImperialEnv

from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize
from stable_baselines3.common.callbacks import BaseCallback, CallbackList

def linear_schedule(initial_value, final_value=1e-5):
    """Linear decay from initial_value to final_value over training."""
    def func(progress_remaining):
        # progress_remaining goes from 1.0 -> 0.0
        return final_value + progress_remaining * (initial_value - final_value)
    return func

class EntCoefScheduleCallback(BaseCallback):
    """
    Custom callback to linearly decay the entropy coefficient during training.
    """
    def __init__(self, initial_ent_coef, final_ent_coef, total_timesteps, verbose=0):
        super().__init__(verbose)
        self.initial_ent_coef = initial_ent_coef
        self.final_ent_coef = final_ent_coef
        self.total_timesteps = total_timesteps

    def _on_step(self) -> bool:
        progress = self.num_timesteps / self.total_timesteps
        progress = min(1.0, max(0.0, progress))
        new_ent_coef = self.initial_ent_coef - progress * (self.initial_ent_coef - self.final_ent_coef)
        self.model.ent_coef = new_ent_coef
        self.logger.record("train/current_ent_coef", new_ent_coef)
        return True

if __name__ == "__main__":
    env = Monitor(ImperialEnv())
    
    # Wrap in DummyVecEnv and VecNormalize
    vec_env = DummyVecEnv([lambda: env])
    import os
    import argparse

    parser = argparse.ArgumentParser(description="Train the Imperial 2030 RL Bot.")
    parser.add_argument("--reset", action="store_true", help="Start training from scratch, ignoring any existing saved model.")
    args = parser.parse_args()

    MODEL_BASENAME = "imperial_ppo_bot"
    MODEL_PATH = f"{MODEL_BASENAME}.zip"
    VEC_NORM_PATH = "vec_normalize.pkl"
    BEST_VEC_NORM_PATH = "vec_normalize_best.pkl"
    BEST_REWARD_PATH = "best_reward.txt"

    if not args.reset and os.path.exists(MODEL_PATH) and os.path.exists(VEC_NORM_PATH):
        print("Found existing model, resuming training...")
        vec_env = VecNormalize.load(VEC_NORM_PATH, vec_env)
        # We must disable training mode when not training, but here we ARE training
        vec_env.training = True
        custom_objects = {
            "learning_rate": linear_schedule(5e-5, 1e-5),
            "n_steps": 8192,
            "batch_size": 512,
            "clip_range": 0.2,
            "ent_coef": 0.03,
            "gamma": 0.995,
            "n_epochs": 6,
            "max_grad_norm": 0.5,
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
            learning_rate=linear_schedule(5e-5, 1e-5),
            n_steps=8192,
            batch_size=512,
            clip_range=0.2,
            ent_coef=0.03,           # Balanced exploration for 64-action masked space
            gamma=0.995,
            n_epochs=6,
            max_grad_norm=0.5,
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
    TOTAL_TIMESTEPS = 3000000
    
    save_callback = SaveOnStepCallback(save_freq=5000, save_path="./", reset=args.reset)
    ent_coef_callback = EntCoefScheduleCallback(initial_ent_coef=0.03, final_ent_coef=0.005, total_timesteps=TOTAL_TIMESTEPS)
    
    callback = CallbackList([save_callback, ent_coef_callback])
    
    model.learn(total_timesteps=TOTAL_TIMESTEPS, reset_num_timesteps=False, callback=callback)

    print("Saving Final Model and VecNormalize statistics...")
    model.save(MODEL_BASENAME)
    vec_env.save(VEC_NORM_PATH)
    print("Training Complete!")
