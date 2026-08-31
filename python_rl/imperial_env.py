import gymnasium as gym
import numpy as np
from gymnasium import spaces
import socket
import json

# Must match RLBotStrategy.StateSize / TotalActionSize in the C# server.
#
# Gymnasium needs the spaces declared in __init__, before the socket has told us anything, so these
# cannot simply be read from the server. They are therefore a *fallback*: the server reports its real
# sizes in the reset response (ResetResponse.stateSize / actionSize) and _check_shapes below fails
# loudly on any mismatch. Rule #17 in .agents/AGENTS.md encourages appending to the state vector, which
# is exactly the change that would otherwise leave these silently stale — the env would keep declaring
# the old width while the server sent the new one, and training would run on misaligned feature indices
# and produce a worthless model with no error anywhere.
DEFAULT_STATE_SIZE = 3172
DEFAULT_ACTION_SIZE = 205


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

        # Curriculum scales pushed in by the trainer via VecEnv.env_method("set_curriculum", ...) and sent
        # to the C# server on every reset. 1.0/1.0 is the historical reward function exactly, so an env
        # nobody ever calls set_curriculum on behaves as it always did.
        self.shaping_scale = 1.0
        self.factory_penalty_scale = 1.0

        self._connect_socket()

        # Actions: 0-6=Rondel, 7=Fight, 8=Retreat, 9-62=BuyBond, 63=Pass, 64-125=Maneuver Select, 126=Maneuver DoNotMove,
        # 127-188=Maneuver Move, 189=DestroyFactory, 190=KeepFactory, 191=StopImport,
        # 192-199=ImportPlace (4 home slots x [Army, Fleet], slot order = nation's home territories sorted by Id),
        # 200=SkipFactoryBuild, 201-204=BuildFactory (4 home slots, same slot order)
        self.action_space = spaces.Discrete(DEFAULT_ACTION_SIZE)

        # State: per-nation totalInterestOwed x6, plus acting-nation investor payment preview x2, etc.
        self.observation_space = spaces.Box(
            low=-np.inf, high=np.inf, shape=(DEFAULT_STATE_SIZE,), dtype=np.float32)

    def _connect_socket(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.connect((self.host, self.port))
        self.sock_file = self.sock.makefile('rw', encoding='utf-8')

    def _request(self, data):
        """One request/response round trip. Raises ConnectionError if the socket is gone."""
        self.sock_file.write(json.dumps(data) + '\n')
        self.sock_file.flush()
        line = self.sock_file.readline()
        if not line:
            raise ConnectionError("Training server closed the connection")
        return json.loads(line)

    def _send_receive(self, data):
        try:
            return self._request(data)
        except (ConnectionError, BrokenPipeError, OSError) as first_error:
            # Only a `reset` may be retried. The server drops a session when its connection dies
            # (TcpTrainingServer: `_sessions.TryRemove(currentSessionId, ...)`), so replaying a `step`
            # on a fresh socket sends a sessionId the server has already discarded — it cannot succeed,
            # and the old code's blind retry turned that into a confusing JSONDecodeError instead of a
            # clear "your episode is gone".
            if data.get("command") != "reset":
                raise ConnectionError(
                    f"Lost connection to the training server mid-episode ({first_error}). The session "
                    f"was discarded server-side; the episode cannot be resumed. Call reset()."
                ) from first_error

            try:
                self.sock.close()
            except OSError:
                pass
            self._connect_socket()
            return self._request(data)

    def _check_shapes(self, res, obs):
        """
        Fails loudly when the server's vector no longer matches what this env declares.

        Deliberately an exception rather than a warning: a size mismatch does not degrade training, it
        invalidates it, and a warning in a long training run scrolls past unread.
        """
        server_state = res.get("stateSize")
        if server_state is not None and server_state != DEFAULT_STATE_SIZE:
            raise ValueError(
                f"Server state vector is {server_state} floats, this env declares {DEFAULT_STATE_SIZE}. "
                f"RLBotStrategy.StateSize changed - update DEFAULT_STATE_SIZE in imperial_env.py "
                f"(see .agents/AGENTS.md rule #17) and retrain; existing .onnx models are unaffected."
            )

        server_actions = res.get("actionSize")
        if server_actions is not None and server_actions != DEFAULT_ACTION_SIZE:
            raise ValueError(
                f"Server action space is {server_actions}, this env declares {DEFAULT_ACTION_SIZE}. "
                f"RLBotStrategy.TotalActionSize changed - update DEFAULT_ACTION_SIZE in imperial_env.py."
            )

        # Also check what actually arrived, which covers servers predating the size fields above.
        if obs.shape[0] != DEFAULT_STATE_SIZE:
            raise ValueError(
                f"Server sent {obs.shape[0]} floats, this env declares {DEFAULT_STATE_SIZE}. "
                f"See .agents/AGENTS.md rule #17."
            )

    def step(self, action):
        if self.session_id is None:
            raise Exception("Call reset() before step()")

        res = self._send_receive({"command": "step", "sessionId": self.session_id, "action": int(action)})

        obs = np.array(res.get("state", []), dtype=np.float32)
        reward = float(res.get("reward", 0.0))
        done = bool(res.get("done", False))

        # A terminal step returns no further observation to validate against.
        if not done:
            self._check_shapes(res, obs)

        self.current_action_mask = np.array(res.get("actionMask", []), dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, reward, done, False, info

    def set_curriculum(self, shaping_scale, factory_penalty_scale):
        """Called through VecEnv.env_method, so it must work across SubprocVecEnv process boundaries -
        hence plain floats and no shared state. Takes effect on the NEXT reset, which is the right
        granularity: changing the reward function underneath a half-played episode would make that
        episode's returns incomparable to both the ones before and after it."""
        self.shaping_scale = float(shaping_scale)
        self.factory_penalty_scale = float(factory_penalty_scale)

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        res = self._send_receive({
            "command": "reset",
            "botType": self.bot_type,
            "opponents": self.opponents,
            "shapingScale": self.shaping_scale,
            "factoryPenaltyScale": self.factory_penalty_scale
        })
        self.session_id = res.get("sessionId")
        obs = np.array(res.get("state", []), dtype=np.float32)
        self._check_shapes(res, obs)

        self.current_action_mask = np.array(res.get("actionMask", []), dtype=np.bool_)
        info = {"action_mask": self.current_action_mask}
        return obs, info

    def action_masks(self):
        return self.current_action_mask
