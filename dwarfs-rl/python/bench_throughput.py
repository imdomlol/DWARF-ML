"""measure environment collection throughput: 1 world vs N worlds in one process.

this isolates exactly what multi-world changes -- how many env steps/s the game
side can produce -- without the PPO/torch stack (which adds per-step inference in
the worker processes but doesn't change as you add worlds). run it for a couple of
world counts and compare:

    python python/bench_throughput.py 1        # one world (single-instance path)
    python python/bench_throughput.py 10       # ten worlds in one process

optional 2nd/3rd args: action_repeat (default 8, the training default) and step
count (default 400). worlds idle-step so nothing dies during the window.
"""

import asyncio
import json
import os
import subprocess
import sys
import time

import websockets

BASE_PORT = 8765


def default_game_exe():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(here, "..", "game-patched", "Dwarfs.exe")


class Server:
    def __init__(self, port):
        self.port = port
        self.ws = None
        self.connected = asyncio.Event()

    async def handler(self, ws):
        self.ws = ws
        self.connected.set()
        try:
            await asyncio.Future()
        except asyncio.CancelledError:
            pass

    async def request(self, payload):
        await self.ws.send(json.dumps(payload))
        return json.loads(await self.ws.recv())


async def run(n, ar, steps):
    servers = [Server(BASE_PORT + i) for i in range(n)]
    cms = [websockets.serve(s.handler, "127.0.0.1", s.port, max_size=None)
           for s in servers]
    for cm in cms:
        await cm.__aenter__()
    try:
        exe = default_game_exe()
        env = dict(os.environ)
        env["DWARFS_BRIDGE_PORT"] = str(BASE_PORT)
        env["DWARFS_BRIDGE_WORLDS"] = str(n)
        env["DWARFS_BRIDGE_GUI"] = "0"
        if "DWARFS_BRIDGE_SERIAL" in os.environ:
            env["DWARFS_BRIDGE_SERIAL"] = os.environ["DWARFS_BRIDGE_SERIAL"]
        print(f"launching one process hosting {n} world(s), action_repeat={ar}"
              + (" [serial]" if env.get("DWARFS_BRIDGE_SERIAL") == "1" else ""))
        game = subprocess.Popen([exe], cwd=os.path.dirname(exe), env=env)

        try:
            await asyncio.wait_for(
                asyncio.gather(*(s.connected.wait() for s in servers)), timeout=120)
        except asyncio.TimeoutError:
            print(f"FAIL: only {sum(s.connected.is_set() for s in servers)}/{n} connected")
            return

        # reset all worlds (distinct seeds), then a short warmup so boot/JIT costs
        # don't land inside the timed window
        # all worlds use the same survivor seed so none dies inside the timed
        # window (this is a throughput bench, not an isolation test)
        await asyncio.gather(*(s.request({
            "command": "RESET", "mode": "m5", "difficulty": "Easy",
            "seed": 42, "action_repeat": ar}) for s in servers))
        for _ in range(20):
            await asyncio.gather(*(s.request({"command": "STEP", "action": 0})
                                   for s in servers))

        # timed window: all worlds idle-step concurrently (same as SubprocVecEnv)
        t0 = time.perf_counter()
        done = 0
        for _ in range(steps):
            states = await asyncio.gather(
                *(s.request({"command": "STEP", "action": 0}) for s in servers))
            done += 1
            if any(st["terminated"] for st in states):
                break
        dt = time.perf_counter() - t0

        world_steps = done * n
        frames = world_steps * ar
        print(f"\n  worlds={n}  steps/world={done}  time={dt:.2f}s")
        print(f"  per-world:  {done / dt:6.1f} env-steps/s   "
              f"{done / dt * ar:7.0f} sim-frames/s")
        print(f"  AGGREGATE:  {world_steps / dt:6.1f} env-steps/s   "
              f"{frames / dt:7.0f} sim-frames/s")

        for s in servers:
            try:
                await s.ws.send(json.dumps({"command": "QUIT"}))
            except Exception:
                pass
    finally:
        try:
            game.terminate()
        except Exception:
            pass
        for cm in cms:
            try:
                await asyncio.wait_for(cm.__aexit__(None, None, None), timeout=3)
            except Exception:
                pass


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 1
    ar = int(sys.argv[2]) if len(sys.argv) > 2 else 8
    steps = int(sys.argv[3]) if len(sys.argv) > 3 else 400
    asyncio.run(run(n, ar, steps))
