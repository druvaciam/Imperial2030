from sb3_contrib import MaskablePPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.monitor import Monitor
from imperial_env import ImperialEnv

if __name__ == "__main__":
    env = Monitor(ImperialEnv())
    
    print("Initializing MaskablePPO Model...")
    model = MaskablePPO("MlpPolicy", env, verbose=1)

    print("Starting Training...")
    # Train for a larger number of timesteps now that the loop is fixed
    model.learn(total_timesteps=50000)

    print("Saving Model...")
    model.save("imperial_ppo_bot")
    print("Training Complete!")
