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

    MODEL_PATH = "imperial_ppo_bot.zip"
    VEC_NORM_PATH = "vec_normalize.pkl"

    if not args.reset and os.path.exists(MODEL_PATH) and os.path.exists(VEC_NORM_PATH):
        print("Found existing model, resuming training...")
        vec_env = VecNormalize.load(VEC_NORM_PATH, vec_env)
        # We must disable training mode when not training, but here we ARE training
        vec_env.training = True
        model = MaskablePPO.load(MODEL_PATH, env=vec_env, verbose=1)
    else:
        print("No existing model found. Initializing new MaskablePPO Model...")
        vec_env = VecNormalize(vec_env, norm_obs=True, norm_reward=True, clip_obs=10.0)
        model = MaskablePPO("MlpPolicy", vec_env, verbose=1)

    from stable_baselines3.common.callbacks import BaseCallback

    class SaveOnStepCallback(BaseCallback):
        def __init__(self, save_freq, save_path, verbose=1):
            super().__init__(verbose)
            self.save_freq = save_freq
            self.save_path = save_path

        def _init_callback(self):
            os.makedirs(self.save_path, exist_ok=True)

        def _on_step(self):
            if self.n_calls % self.save_freq == 0:
                self.model.save(os.path.join(self.save_path, "imperial_ppo_bot"))
                self.training_env.save(os.path.join(self.save_path, "vec_normalize.pkl"))
                if self.verbose > 0:
                    print(f"Saved model and normalize stats at step {self.num_timesteps}")
            return True

    print("Starting Training...")
    # Train for a larger number of timesteps.
    # It will automatically save every 10,000 steps to the current directory
    callback = SaveOnStepCallback(save_freq=10000, save_path="./")
    model.learn(total_timesteps=1000000, reset_num_timesteps=False, callback=callback)

    print("Saving Final Model and VecNormalize statistics...")
    model.save("imperial_ppo_bot")
    vec_env.save("vec_normalize.pkl")
    print("Training Complete!")
