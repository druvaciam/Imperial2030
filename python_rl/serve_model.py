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

@app.route("/predict", methods=["POST"])
def predict():
    data = request.json
    state = np.array(data["state"], dtype=np.float32)
    
    if model is not None:
        action, _states = model.predict(state, deterministic=True)
        return jsonify({"action": int(action)})
    else:
        # Fallback to random if not trained yet
        return jsonify({"action": int(np.random.randint(0, 8))})

if __name__ == "__main__":
    app.run(port=5001)
