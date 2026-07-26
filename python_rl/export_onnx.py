import torch
import onnx
import json
import os
import numpy as np
from sb3_contrib import MaskablePPO
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize
from imperial_env import ImperialEnv

model_path = "imperial_ppo_bot_best"
vec_env_path = "vec_normalize_best.pkl"

if not os.path.exists(model_path + ".zip"):
    print(f"Error: {model_path}.zip not found.")
    exit(1)

print(f"Loading {model_path}...")
model = MaskablePPO.load(model_path)

# Wrapper class to just extract logits for the ONNX graph
class OnnxablePolicy(torch.nn.Module):
    def __init__(self, policy):
        super().__init__()
        self.policy = policy

    def forward(self, observation):
        # Pass observation through feature extractor
        features = self.policy.extract_features(observation)
        # Pass through the pi (actor) MLP
        latent_pi, _ = self.policy.mlp_extractor(features)
        # Pass through the action net to get logits (before distribution/masking)
        logits = self.policy.action_net(latent_pi)
        return logits

onnx_policy = OnnxablePolicy(model.policy)

# Create a dummy tensor of the correct shape (batch_size, obs_dim)
obs_dim = model.observation_space.shape[0]
dummy_input = torch.randn(1, obs_dim)

onnx_path = "imperial_ppo_bot.onnx"
print(f"Exporting ONNX model to {onnx_path}...")
torch.onnx.export(
    onnx_policy,
    dummy_input,
    onnx_path,
    opset_version=14,
    input_names=["input"],
    output_names=["output"],
    dynamic_axes={"input": {0: "batch_size"}, "output": {0: "batch_size"}}
)
print("ONNX export complete.")

# Now extract the VecNormalize statistics
if os.path.exists(vec_env_path):
    print(f"Loading {vec_env_path}...")
    class MockEnv(ImperialEnv):
        def _connect_socket(self):
            pass # Prevent TCP connection attempt during export
            
    dummy_env = DummyVecEnv([lambda: MockEnv()])
    vec_env = VecNormalize.load(vec_env_path, dummy_env)
    
    mean = vec_env.obs_rms.mean.tolist()
    var = vec_env.obs_rms.var.tolist()
    epsilon = vec_env.epsilon
    
    json_path = "vec_normalize.json"
    print(f"Exporting normalization stats to {json_path}...")
    with open(json_path, "w") as f:
        json.dump({"mean": mean, "var": var, "epsilon": epsilon}, f, indent=4)
    print("Stats export complete.")
else:
    print(f"Warning: {vec_env_path} not found.")
