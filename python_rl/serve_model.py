from flask import Flask, request, jsonify
from sb3_contrib import MaskablePPO
import numpy as np
import os

app = Flask(__name__)

model_path_best = "imperial_ppo_bot_best.zip"
model_path_latest = "imperial_ppo_bot.zip"
model = None

# Prioritize the best model, fallback to the latest checkpoint
active_model_path = model_path_best if os.path.exists(model_path_best) else model_path_latest

if os.path.exists(active_model_path):
    print(f"Loading Trained MaskablePPO Model from {active_model_path}...")
    model = MaskablePPO.load(active_model_path)
else:
    print("Warning: Model not found. Will return random actions.")

from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize
from imperial_env import ImperialEnv

vec_env_path_best = "vec_normalize_best.pkl"
vec_env_path_latest = "vec_normalize.pkl"
vec_env = None

# Prioritize the best stats, fallback to the latest stats
active_vec_path = vec_env_path_best if os.path.exists(vec_env_path_best) else vec_env_path_latest

if os.path.exists(active_vec_path):
    print(f"Loading VecNormalize statistics from {active_vec_path}...")
    dummy_env = DummyVecEnv([lambda: ImperialEnv()])
    vec_env = VecNormalize.load(active_vec_path, dummy_env)
    vec_env.training = False
    vec_env.norm_reward = False

@app.route("/predict", methods=["POST"])
def predict():
    data = request.json
    state = np.array(data["state"], dtype=np.float32)
    action_mask = np.array(data.get("actionMask", [True]*10), dtype=np.bool_)
    
    if vec_env is not None:
        pass # We no longer normalize observation using VecNormalize since it's manually normalized in C#

    if model is not None:
        action, _states = model.predict(state, action_masks=action_mask, deterministic=True)
        return jsonify({"action": int(action)})
    else:
        # Fallback to random valid action
        valid_actions = np.where(action_mask)[0]
        if len(valid_actions) > 0:
            return jsonify({"action": int(np.random.choice(valid_actions))})
        return jsonify({"action": 0})

if __name__ == "__main__":
    app.run(port=5001)
