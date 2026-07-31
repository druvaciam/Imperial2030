import gymnasium as gym
import numpy as np
from gymnasium import spaces
import socket
import json

class ImperialEnv(gym.Env):
    """Custom Environment that follows gym interface"""
    metadata = {'render.modes': ['human']}

    def __init__(self, host="127.0.0.1", port=5295):
        super(ImperialEnv, self).__init__()
        self.host = host
        self.port = port
        self.session_id = None
        
        self._connect_socket()
        
        # Actions: 0-6=Rondel, 7=Fight, 8=Retreat, 9-62=BuyBond(Nation 0-5, Cost 0-8), 63=Pass
        self.action_space = spaces.Discrete(64)
        
        # State: 591 floats
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(591,), dtype=np.float32)

    def _connect_socket(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.connect((self.host, self.port))
        self.sock_file = self.sock.makefile('rw', encoding='utf-8')

    def _send_receive(self, data):
        try:
            self.sock_file.write(json.dumps(data) + '\n')
            self.sock_file.flush()
            line = self.sock_file.readline()
            if not line:
                raise ConnectionError("Connection lost")
            return json.loads(line)
        except (ConnectionError, BrokenPipeError):
            self.sock.close()
            self._connect_socket()
            self.sock_file.write(json.dumps(data) + '\n')
            self.sock_file.flush()
            return json.loads(self.sock_file.readline())

    def step(self, action):
        if self.session_id is None:
            raise Exception("Call reset() before step()")

        res = self._send_receive({"command": "step", "sessionId": self.session_id, "action": int(action)})
        
        obs = np.array(res.get("state", []), dtype=np.float32)
        reward = float(res.get("reward", 0.0))
        done = bool(res.get("done", False))
        
        self.current_action_mask = np.array(res.get("actionMask", []), dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, reward, done, False, info

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        res = self._send_receive({"command": "reset"})
        self.session_id = res.get("sessionId")
        obs = np.array(res.get("state", []), dtype=np.float32)
        
        self.current_action_mask = np.array(res.get("actionMask", []), dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, info

    def action_masks(self):
        return self.current_action_mask

