"""gymnasium environment for the modded game, ready to train against.

runs the websocket server the mod connects to and turns RESET/STEP into the
standard gym api. observation is a dict, the 40x60 terrain window, matching 40x60
dwarf and enemy layers (where the units are) and a small stats vector (gold,
dwarves, time left, city hp). action is MultiDiscrete,
what to do + where, (14, 60, 40) = (action, x col, y row). action 0 is idle,
1 dynamite, 2 wall, 3 to 6 a green arrow up/right/down/left, 7 place tower,
8 reinforce wall, 9 to 13 are tower actions (toggle digger, spawn warrior,
recall, cannon, toggle train). the tile picks the tower for 9 to 13, and for
12 the cannon its the target to fire at. see docs/PROTOCOL.md for the rest.

mode, difficulty, seed, action_repeat and the reward knobs all get picked per
env / per reset, see docs/PROTOCOL.md for what they mean.
"""

import asyncio
import json
import os
import queue
import subprocess
import threading
import time

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
        except Exception:
            pass  # game closed / quit, the connection dropping is expected
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

    @property
    def connected(self):
        # whether a game is currently attached (set on connect, cleared on drop)
        return self._connected.is_set()


class DwarfsEnv(gym.Env):
    metadata = {"render_modes": ["human"]}

    def __init__(self, port=8765, mode="m5", difficulty="Easy",
                 action_repeat=8, death_penalty=1500.0, invalid_action=0.0,
                 hazard_penalty=0.0, render=False, render_fps=0,
                 game_exe=None, launch_delay=0.0):
        super().__init__()
        self.mode = mode
        self.difficulty = difficulty
        self.action_repeat = action_repeat
        self.death_penalty = death_penalty
        self.invalid_action = invalid_action
        self.hazard_penalty = hazard_penalty

        # grid is the terrain layer, dwarves and enemies are matching layers
        # marking the units (0 none, 1/2 by type) so the model can see where they
        # are not just how many. dwarves 1 digger 2 warrior, enemies 1 minion 2
        # boss. enemies only shows what a human can see, hidden ones are masked.
        # stats is the small scalar vector
        self.observation_space = gym.spaces.Dict({
            "grid": gym.spaces.Box(0, 6, shape=(GRID_H, GRID_W), dtype=np.int32),
            "dwarves": gym.spaces.Box(0, 2, shape=(GRID_H, GRID_W), dtype=np.int32),
            "enemies": gym.spaces.Box(0, 2, shape=(GRID_H, GRID_W), dtype=np.int32),
            "stats": gym.spaces.Box(-np.inf, np.inf, shape=(4,), dtype=np.float32),
        })
        # action, x column, y row. idle ignores the coordinates.
        # 14 action types: 0 idle, 1 dynamite, 2 wall, 3-6 arrow up/right/down/left,
        # 7 place tower, 8 reinforce wall, 9-13 tower actions (toggle digger, spawn
        # warrior, recall, cannon, toggle train). matches docs/PROTOCOL.md and the
        # mod's ApplyAction
        self.action_space = gym.spaces.MultiDiscrete([14, GRID_W, GRID_H])

        self._bridge = _Bridge(port)
        # episodes default to headless on the mod side, the reset carries these
        # so whatever you pick here sticks across episodes
        self._render = bool(render)
        self._render_fps = int(render_fps)

        # if a game exe is handed in, this env launches and owns one game process
        # on its own port. thats what lets a vec env be self contained, one game
        # per worker and no control panel windows. the bridge server is already
        # up above so the game connects the moment it boots
        self._game = None
        if game_exe:
            if launch_delay > 0:
                time.sleep(launch_delay)  # stagger so the steam_appid.txt writes dont clash
            childenv = dict(os.environ)
            childenv["DWARFS_BRIDGE_PORT"] = str(port)
            childenv["DWARFS_BRIDGE_GUI"] = "0"
            self._game = subprocess.Popen([game_exe],
                                          cwd=os.path.dirname(game_exe), env=childenv)

    def _unpack(self, state):
        grid = np.asarray(state["map_grid"], dtype=np.int32).reshape(GRID_H, GRID_W)
        dwarves = np.asarray(state["dwarf_grid"], dtype=np.int32).reshape(GRID_H, GRID_W)
        enemies = np.asarray(state["enemy_grid"], dtype=np.int32).reshape(GRID_H, GRID_W)
        stats = np.array([state["gold"], state["dwarves"],
                          state["time_left"], state["city_hp"]],
                         dtype=np.float32)
        info = {"score": state["score"], "action_ok": state["action_ok"],
                "crop": (state["crop_x"], state["crop_y"]), "tick": state["tick"]}
        return {"grid": grid, "dwarves": dwarves, "enemies": enemies,
                "stats": stats}, info

    def reset(self, seed=None, options=None, timeout=None):
        super().reset(seed=seed)
        cmd = {"command": "RESET", "mode": self.mode,
               "difficulty": self.difficulty,
               "action_repeat": self.action_repeat,
               "death_penalty": self.death_penalty,
               "invalid_action": self.invalid_action,
               "hazard_penalty": self.hazard_penalty,
               "render": self._render,
               "render_fps": self._render_fps}
        if seed is not None:
            cmd["seed"] = int(seed)
        # timeout is optional so callers like the headless probe can bound how long
        # they wait for a fresh game to come up (a failed one freezes on the
        # device-creation modal and never replies); training leaves it at default
        state = self._bridge.request(cmd, timeout=timeout or 120)
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
        # only ask nicely if a game is actually attached; otherwise the QUIT send
        # would block waiting up to 60s for a connection that never comes (e.g. a
        # probe instance that failed device creation), so go straight to the kill
        if self._bridge.connected:
            try:
                self._bridge.send({"command": "QUIT"})
            except Exception:
                pass  # game already gone, fine
        if self._game is not None:
            try:
                self._game.wait(timeout=8)  # QUIT should bring it down on its own
            except Exception:
                self._game.kill()
            self._game = None


def default_game_exe():
    # game-patched sits next to python/ under dwarfs-rl/
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(here, "..", "game-patched", "Dwarfs.exe")


def make_vec_env(n, base_port=8765, stagger=4.0, game_exe=None, **kwargs):
    # n game instances, each in its own subprocess env, all stepping together.
    # this is what actually uses more of the pc, roughly one game per core. needs
    # stable_baselines3. extra kwargs (mode, difficulty, action_repeat, reward
    # weights) pass straight through to every DwarfsEnv
    from stable_baselines3.common.vec_env import SubprocVecEnv
    if game_exe is None:
        game_exe = default_game_exe()

    def factory(i):
        def _make():
            return DwarfsEnv(port=base_port + i, game_exe=game_exe,
                             launch_delay=i * stagger, **kwargs)
        return _make

    return SubprocVecEnv([factory(i) for i in range(n)])
