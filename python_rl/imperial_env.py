import gymnasium as gym
import numpy as np
from gymnasium import spaces
import requests
import urllib3
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

class ImperialEnv(gym.Env):
    """Custom Environment that follows gym interface"""
    metadata = {'render.modes': ['human']}

    def __init__(self, api_url="http://localhost:5294/api/training"):
        super(ImperialEnv, self).__init__()
        self.api_url = api_url
        self.session_id = None
        
        # Actions: 0=Factory, 1=Production, 2=Import, 3=ManeuverAggro, 4=ManeuverDef, 5=Taxation, 6=Investor, 7=BuyBond
        self.action_space = spaces.Discrete(8)
        
        # State: 32 floats
        self.observation_space = spaces.Box(low=-1000, high=1000, shape=(32,), dtype=np.float32)

    def step(self, action):
        if self.session_id is None:
            raise Exception("Call reset() before step()")

        res = requests.post(f"{self.api_url}/step", json={"SessionId": self.session_id, "Action": int(action)}, verify=False).json()
        
        obs = np.array(res["state"], dtype=np.float32)
        reward = float(res["reward"])
        done = bool(res["done"])
        
        self.current_action_mask = np.array(res["actionMask"], dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, reward, done, False, info

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        res = requests.post(f"{self.api_url}/reset", verify=False).json()
        self.session_id = res["sessionId"]
        obs = np.array(res["state"], dtype=np.float32)
        
        self.current_action_mask = np.array(res["actionMask"], dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, info

    def action_masks(self):
        return self.current_action_mask

