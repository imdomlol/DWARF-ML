"""gymnasium environment for the modded game, ready to train against.

runs the websocket server the mod connects to and turns RESET/STEP into the
standard gym api. observation is a dict with 4 cameras (cam0-cam3, each shape
(3, 30, 30) stacking terrain/dwarves/enemies) following the northmost/eastmost/
southmost/westmost digger dwarf, plus a stats vector (9 scalars). cameras are
zero-filled when fewer than 4 diggers exist. action is MultiDiscrete,
(14, 4, 30, 30) = (action_type, camera, x_col, y_row). action 0 is idle, 1
dynamite, 2 wall, 3 to 6 a green arrow up/right/down/left, 7 place tower, 8
reinforce wall, 9 to 13 are tower actions (toggle digger, spawn warrior, recall,
cannon, toggle train). see docs/PROTOCOL.md for the full protocol.

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

CAM_W = 30   # camera window width  (tiles)
CAM_H = 30   # camera window height (tiles)


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
                 hazard_penalty=0.0, instant_seal=0.0, render=False, render_fps=0,
                 game_exe=None, launch_delay=0.0):
        super().__init__()
        self.mode = mode
        self.difficulty = difficulty
        self.action_repeat = action_repeat
        self.death_penalty = death_penalty
        self.invalid_action = invalid_action
        self.hazard_penalty = hazard_penalty
        self.instant_seal = instant_seal

        # 4 cameras (0=north 1=east 2=south 3=west digger), each (3, CAM_H, CAM_W):
        # channel 0 = terrain (0-6), channel 1 = dwarves (0-2), channel 2 = enemies (0-2).
        # cameras are zero-filled when fewer than 4 diggers exist. stats is the
        # 9-scalar vector (gold, dwarves, time_left, city_hp, 5 costs)
        self.observation_space = gym.spaces.Dict({
            "cam0": gym.spaces.Box(0, 6, shape=(3, CAM_H, CAM_W), dtype=np.int32),
            "cam1": gym.spaces.Box(0, 6, shape=(3, CAM_H, CAM_W), dtype=np.int32),
            "cam2": gym.spaces.Box(0, 6, shape=(3, CAM_H, CAM_W), dtype=np.int32),
            "cam3": gym.spaces.Box(0, 6, shape=(3, CAM_H, CAM_W), dtype=np.int32),
            "stats": gym.spaces.Box(-np.inf, np.inf, shape=(9,), dtype=np.float32),
        })
        # action_type (14), camera 0-3, x column (0-29), y row (0-29)
        self.action_space = gym.spaces.MultiDiscrete([14, 4, CAM_W, CAM_H])

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
        def cam(i):
            terrain = np.asarray(state[f"cam{i}_terrain"], dtype=np.int32).reshape(CAM_H, CAM_W)
            dwarves = np.asarray(state[f"cam{i}_dwarves"], dtype=np.int32).reshape(CAM_H, CAM_W)
            enemies = np.asarray(state[f"cam{i}_enemies"], dtype=np.int32).reshape(CAM_H, CAM_W)
            return np.stack([terrain, dwarves, enemies], axis=0)  # (3, CAM_H, CAM_W)
        stats = np.array([state["gold"], state["dwarves"],
                          state["time_left"], state["city_hp"],
                          state["cost_wall"], state["cost_dynamite"],
                          state["cost_arrow"], state["cost_tower"],
                          state["cost_warrior"]],
                         dtype=np.float32)
        info = {"score": state["score"], "action_ok": state["action_ok"],
                "cam_origins": state["cam_origins"],
                "cave_opened": state["cave_opened"],
                "cave_x": state["cave_x"], "cave_y": state["cave_y"],
                "instant_seal_delta": state["instant_seal_delta"],
                "tick": state["tick"]}
        return {"cam0": cam(0), "cam1": cam(1), "cam2": cam(2), "cam3": cam(3),
                "stats": stats}, info

    def reset(self, seed=None, options=None, timeout=None):
        super().reset(seed=seed)
        cmd = {"command": "RESET", "mode": self.mode,
               "difficulty": self.difficulty,
               "action_repeat": self.action_repeat,
               "death_penalty": self.death_penalty,
               "invalid_action": self.invalid_action,
               "hazard_penalty": self.hazard_penalty,
               "instant_seal": self.instant_seal,
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
                                      "camera": int(a[1]),
                                      "x": int(a[2]), "y": int(a[3])})
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


def make_world_env(n, base_port=8765, game_exe=None, **kwargs):
    # multi-world (Path C): ONE patched game process hosting n worlds, with n env
    # workers connecting to it. unlike make_vec_env (n separate processes, one GPU
    # device each, capped at the 2-3 the card hands out), this launches a single
    # game with DWARFS_BRIDGE_WORLDS=n. that process owns one real device and runs
    # n independent worlds in-process, so the parallel env count scales on CPU/RAM
    # instead of the graphics card. each worker is a plain DwarfsEnv connecting on
    # its own port; none of them launches a game. extra kwargs pass through to
    # every DwarfsEnv just like make_vec_env.
    from stable_baselines3.common.vec_env import SubprocVecEnv, VecEnvWrapper
    if game_exe is None:
        game_exe = default_game_exe()

    def factory(i):
        def _make():
            # game_exe stays None: workers only connect, the one process below
            # hosts every world
            return DwarfsEnv(port=base_port + i, **kwargs)
        return _make

    # stand the n websocket servers up first (one per worker), THEN launch the
    # single game so the mod finds every port on its first connect sweep
    venv = SubprocVecEnv([factory(i) for i in range(n)])

    childenv = dict(os.environ)
    childenv["DWARFS_BRIDGE_PORT"] = str(base_port)
    childenv["DWARFS_BRIDGE_WORLDS"] = str(n)
    childenv["DWARFS_BRIDGE_GUI"] = "0"
    game = subprocess.Popen([game_exe], cwd=os.path.dirname(game_exe), env=childenv)

    class _MultiWorldVecEnv(VecEnvWrapper):
        # owns the single game process so closing the vec env brings it down too
        def __init__(self, venv, game):
            super().__init__(venv)
            self._game = game

        def reset(self):
            return self.venv.reset()

        def step_wait(self):
            return self.venv.step_wait()

        def close(self):
            self.venv.close()
            if self._game is not None:
                try:
                    self._game.wait(timeout=8)
                except Exception:
                    self._game.kill()
                self._game = None

    return _MultiWorldVecEnv(venv, game)
