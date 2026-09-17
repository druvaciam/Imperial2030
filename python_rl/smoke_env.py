"""Drive the training server with random MASKED actions for a fixed number of steps.

    python smoke_env.py [steps=6000] [seed=7]

Run against a server started with `--training` (port 5295). Writes NOTHING to python_rl/ - no SB3, no
checkpoint, no vec_normalize.pkl, no best_reward.txt - so it is safe to run next to a real training
setup. It exercises the same TCP step path train.py uses, so a server-side throw shows up here as
ConnectionError exactly as it does in a real run; the cause is then in the C# server log.

Why random actions: they reach the rare orderings a trained policy avoids. On 2026-09-15 five seeds of
10k steps found two bugs in the training step handler that 476 unit tests had not - an Import sequence
outliving its turn after a mid-turn government change, and a bot repeating a slot action its
predecessor had already taken - each within the first 20k steps.
"""
import os, sys, time, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from imperial_env import ImperialEnv

rng = np.random.default_rng(int(sys.argv[2]) if len(sys.argv) > 2 else 7)
target_steps = int(sys.argv[1]) if len(sys.argv) > 1 else 6000
env = ImperialEnv(bot_type="RL-4")
obs, info = env.reset()
steps = episodes = 0
kinds = collections.Counter()
t0 = time.time()
while steps < target_steps:
    mask = env.action_masks()
    legal = np.flatnonzero(mask)
    a = int(rng.choice(legal)) if len(legal) else 0
    # classify so the summary shows the crossing-into-Import/Factory cases were actually exercised
    if a <= 5: kinds["rondel"] += 1
    elif a == 63: kinds["pass/endphase"] += 1
    elif 9 <= a <= 62: kinds["invest"] += 1
    elif 126 <= a <= 188: kinds["maneuver"] += 1
    elif 189 <= a <= 191: kinds["destroy/keep/stopimport"] += 1
    elif 192 <= a <= 199: kinds["import-place"] += 1
    elif 200 <= a: kinds["factory"] += 1
    else: kinds["other"] += 1
    obs, r, term, trunc, info = env.step(a)
    steps += 1
    if term or trunc:
        episodes += 1
        obs, info = env.reset()
env.close()
print(f"OK steps={steps} episodes_finished={episodes} elapsed={time.time()-t0:.0f}s")
print("action kinds:", dict(kinds))
