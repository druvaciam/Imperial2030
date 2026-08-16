import gymnasium as gym
import numpy as np
from gymnasium import spaces
import socket
import json

class ImperialEnv(gym.Env):
    """Custom Environment that follows gym interface"""
    metadata = {'render.modes': ['human']}

    def __init__(self, host="127.0.0.1", port=5295, bot_type="RL", opponents=None):
        super(ImperialEnv, self).__init__()
        self.host = host
        self.port = port
        self.bot_type = bot_type
        self.opponents = opponents if opponents is not None else []
        self.session_id = None
        
        self._connect_socket()
        
        # Actions: 0-6=Rondel, 7=Fight, 8=Retreat, 9-62=BuyBond, 63=Pass, 64-125=Maneuver Select, 126=Maneuver DoNotMove,
        # 127-188=Maneuver Move, 189=DestroyFactory, 190=KeepFactory, 191=StopImport,
        # 192-199=ImportPlace (4 home slots x [Army, Fleet], slot order = nation's home territories sorted by Id),
        # 200=SkipFactoryBuild, 201-204=BuildFactory (4 home slots, same slot order)
        self.action_space = spaces.Discrete(205)
        
        # State: 3164 floats
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(3164,), dtype=np.float32)

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
        res = self._send_receive({
            "command": "reset", 
            "botType": self.bot_type,
            "opponents": self.opponents
        })
        self.session_id = res.get("sessionId")
        obs = np.array(res.get("state", []), dtype=np.float32)
        
        self.current_action_mask = np.array(res.get("actionMask", []), dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, info

    def action_masks(self):
        return self.current_action_mask

