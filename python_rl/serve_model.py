from flask import Flask, request, jsonify
from sb3_contrib import MaskablePPO
import numpy as np
import os

app = Flask(__name__)

model_path = "imperial_ppo_bot.zip"
model = None

if os.path.exists(model_path):
    print("Loading Trained MaskablePPO Model...")
    model = MaskablePPO.load("imperial_ppo_bot")
else:
    print("Warning: Model not found. Will return random actions.")

from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize
from imperial_env import ImperialEnv

vec_env_path = "vec_normalize.pkl"
vec_env = None
if os.path.exists(vec_env_path):
    print("Loading VecNormalize statistics...")
    dummy_env = DummyVecEnv([lambda: ImperialEnv()])
    vec_env = VecNormalize.load(vec_env_path, dummy_env)
    vec_env.training = False
    vec_env.norm_reward = False

@app.route("/predict", methods=["POST"])
def predict():
    data = request.json
    state = np.array(data["state"], dtype=np.float32)
    action_mask = np.array(data.get("actionMask", [True]*10), dtype=np.bool_)
    
    if vec_env is not None:
        # VecNormalize expects shape (n_envs, n_features)
        state = vec_env.normalize_obs(state.reshape(1, -1))[0]

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
