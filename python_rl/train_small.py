from sb3_contrib import MaskablePPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor
from imperial_env import ImperialEnv

from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize

if __name__ == "__main__":
    env = Monitor(ImperialEnv())
    
    # Wrap in DummyVecEnv and VecNormalize
    vec_env = DummyVecEnv([lambda: env])
    import os
    import argparse

    parser = argparse.ArgumentParser(description="Train the Imperial 2030 RL Bot.")
    parser.add_argument("--reset", action="store_true", help="Start training from scratch, ignoring any existing saved model.")
    args = parser.parse_args()

    MODEL_BASENAME = "imperial_ppo_bot_small"
    MODEL_PATH = f"{MODEL_BASENAME}.zip"
    VEC_NORM_PATH = "vec_normalize_small.pkl"
    BEST_VEC_NORM_PATH = "vec_normalize_best_small.pkl"
    BEST_REWARD_PATH = "best_reward_small.txt"

    if not args.reset and os.path.exists(MODEL_PATH) and os.path.exists(VEC_NORM_PATH):
        print("Found existing model, resuming training...")
        vec_env = VecNormalize.load(VEC_NORM_PATH, vec_env)
        # We must disable training mode when not training, but here we ARE training
        vec_env.training = True
        custom_objects = {
            "learning_rate": 1e-4,
            "n_steps": 4096,
            "batch_size": 128
        }
        model = MaskablePPO.load(MODEL_PATH, env=vec_env, custom_objects=custom_objects, verbose=1)
    else:
        print("No existing model found. Initializing new MaskablePPO Model...")
        vec_env = VecNormalize(vec_env, norm_obs=True, norm_reward=True, clip_obs=10.0)
        
        policy_kwargs = dict(
            net_arch=dict(
                pi=[256, 256],
                vf=[256, 256],
            )
        )
        
        model = MaskablePPO(
            "MlpPolicy", 
            vec_env, 
            policy_kwargs=policy_kwargs, 
            learning_rate=1e-4,      # Lower learning rate to prevent thrashing (high KL div)
            n_steps=4096,            # Larger rollout buffer
            batch_size=128,          # Larger batch size for more stable gradients
            verbose=1
        )

    import numpy as np
    from stable_baselines3.common.callbacks import BaseCallback

    class SaveOnStepCallback(BaseCallback):
        def __init__(self, save_freq, save_path, verbose=1):
            super().__init__(verbose)
            self.save_freq = save_freq
            self.save_path = save_path
            self.best_mean_reward = -np.inf
            self.best_reward_file = os.path.join(save_path, BEST_REWARD_PATH)
            if os.path.exists(self.best_reward_file):
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
                if self.verbose > 0:
                    print(f"Saved latest checkpoint at step {self.num_timesteps}")

                # Check if we have a new best model
                if len(self.model.ep_info_buffer) > 0:
                    mean_reward = np.mean([ep_info["r"] for ep_info in self.model.ep_info_buffer])
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
    callback = SaveOnStepCallback(save_freq=5000, save_path="./")
    model.learn(total_timesteps=1000000, reset_num_timesteps=False, callback=callback)

    print("Saving Final Model and VecNormalize statistics...")
    model.save(MODEL_BASENAME)
    vec_env.save(VEC_NORM_PATH)
    print("Training Complete!")
