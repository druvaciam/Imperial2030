import torch
import onnx
import json
import os
import numpy as np
from sb3_contrib import MaskablePPO
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize
from imperial_env import ImperialEnv

import argparse

parser = argparse.ArgumentParser()
parser.add_argument("--bot-type", type=str, default="RL", help="The name of the bot to export (e.g. RL, RL-2).")
args = parser.parse_args()

model_path = f"{args.bot_type}_best"
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

onnx_path = f"{args.bot_type}.onnx"
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


