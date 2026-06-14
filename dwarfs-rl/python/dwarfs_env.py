"""gymnasium environment for the modded game, ready to train against.

runs the websocket server the mod connects to and turns RESET/STEP into the
standard gym api. observation is a dict, the 40x60 map window plus a small
stats vector (gold, dwarves, time left, city hp). action is MultiDiscrete,
what to do + where, (7, 60, 40) = (action, x col, y row). action 0 is idle,
1 dynamite, 2 wall, 3 to 6 a green arrow pointing up/right/down/left.

mode, difficulty, seed, action_repeat and the reward knobs all get picked per
env / per reset, see PROTOCOL.md for what they mean.
"""

import asyncio
import json
import queue
import threading

import gymnasium as gym
import numpy as np
import websockets

GRID_W = 60
GRID_H = 40


class _Bridge:
    """owns the websocket server and the background asyncio loop so the env
    itself can stay plain and synchronous. one game connects per port."""

    def __init__(self, port):
        self.port = port
        self._inbox = queue.Queue()
        self._conn = None
        self._connected = threading.Event()
        self._loop = asyncio.new_event_loop()
        t = threading.Thread(target=self._run, daemon=True)
        t.start()

    def _run(self):
        asyncio.set_event_loop(self._loop)
        self._loop.run_until_complete(self._serve())

    async def _serve(self):
        async with websockets.serve(self._handler, "127.0.0.1", self.port,
                                    max_size=None):
            await asyncio.Future()

    async def _handler(self, ws):
        # the mod reconnects on its own if anything drops so just take the
        # latest connection and shovel its messages into the inbox
        self._conn = ws
        self._connected.set()
        try:
            async for msg in ws:
                self._inbox.put(msg)
        finally:
            self._connected.clear()
            self._conn = None

    def send(self, payload):
        self._connected.wait(timeout=60)
        if self._conn is None:
            raise RuntimeError(
                f"no game connected on port {self.port}, launch the patched "
                f"game with DWARFS_BRIDGE_PORT={self.port}")
        fut = asyncio.run_coroutine_threadsafe(
            self._conn.send(json.dumps(payload)), self._loop)
        fut.result(timeout=10)

    def request(self, payload, timeout=120):
        self.send(payload)
        return json.loads(self._inbox.get(timeout=timeout))


class DwarfsEnv(gym.Env):
    metadata = {"render_modes": ["human"]}

    def __init__(self, port=8765, mode="m5", difficulty="Easy",
                 action_repeat=8, death_penalty=1500.0, invalid_action=0.0,
                 render=False, render_fps=0):
        super().__init__()
        self.mode = mode
        self.difficulty = difficulty
        self.action_repeat = action_repeat
        self.death_penalty = death_penalty
        self.invalid_action = invalid_action

        self.observation_space = gym.spaces.Dict({
            "grid": gym.spaces.Box(0, 6, shape=(GRID_H, GRID_W), dtype=np.int32),
            "stats": gym.spaces.Box(-np.inf, np.inf, shape=(4,), dtype=np.float32),
        })
        # action, x column, y row. idle ignores the coordinates
        self.action_space = gym.spaces.MultiDiscrete([7, GRID_W, GRID_H])

        self._bridge = _Bridge(port)
        # episodes default to headless on the mod side, the reset carries these
        # so whatever you pick here sticks across episodes
        self._render = bool(render)
        self._render_fps = int(render_fps)

    def _unpack(self, state):
        grid = np.asarray(state["map_grid"], dtype=np.int32).reshape(GRID_H, GRID_W)
        stats = np.array([state["gold"], state["dwarves"],
                          state["time_left"], state["city_hp"]],
                         dtype=np.float32)
        info = {"score": state["score"], "action_ok": state["action_ok"],
                "crop": (state["crop_x"], state["crop_y"]), "tick": state["tick"]}
        return {"grid": grid, "stats": stats}, info

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        cmd = {"command": "RESET", "mode": self.mode,
               "difficulty": self.difficulty,
               "action_repeat": self.action_repeat,
               "death_penalty": self.death_penalty,
               "invalid_action": self.invalid_action,
               "render": self._render,
               "render_fps": self._render_fps}
        if seed is not None:
            cmd["seed"] = int(seed)
        state = self._bridge.request(cmd)
        obs, info = self._unpack(state)
        return obs, info

    def step(self, action):
        a = np.asarray(action).ravel()
        state = self._bridge.request({"command": "STEP", "action": int(a[0]),
                                      "x": int(a[1]), "y": int(a[2])})
        obs, info = self._unpack(state)
        return (obs, float(state["immediate_reward"]),
                bool(state["terminated"]), bool(state["truncated"]), info)

    def set_render(self, enabled, max_fps=0):
        # flip drawing on mid run to watch the agent, 60 fps = real time.
        # remembered so the next reset doesnt switch it back off
        self._render = bool(enabled)
        self._render_fps = int(max_fps)
        self._bridge.send({"command": "RENDER", "enabled": self._render,
                           "max_fps": self._render_fps})

    def close(self):
        try:
            self._bridge.send({"command": "QUIT"})
        except Exception:
            pass  # game already gone, fine
