"""multi-world (Path C) smoke + isolation + parity test.

launches ONE patched game process that hosts N worlds in-process (one device,
shared content), each talking to its own env worker on its own port. proves the
production multi-world path end to end:

  * isolation + determinism: two worlds reset with the SAME seed must stay
    byte-identical for a whole run even while their siblings run other seeds.
  * divergence: a different seed gives a different world.
  * parity: a detached world matches the values a normal single-instance game
    produces for the same seed (Easy / m5 / seed 42).
  * throughput: aggregate steps/s across all N worlds in the one process.

run this directly (it launches the game itself):
    python python/multiworld_test.py [N]      # N worlds, default 4
"""

import asyncio
import json
import os
import subprocess
import sys
import time

import websockets


def default_game_exe():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(here, "..", "game-patched", "Dwarfs.exe")


BASE_PORT = 8765
STEPS = 400
ACTION_REPEAT = 4

# what a normal single-instance Easy / m5 / seed-42 game reports on its first
# observation (captured from fake_env.py against a plain patched game). a detached
# world built for the same seed must match these for us to trust the numbers.
#
# note on time_left: GenerateLevel sets m_iTimeLeft = 18900. a detached world
# reports that pristine value on its first obs; a normal game reports 18899
# because its menu->game fade burns one frame before the first obs. so we allow a
# 1-tick slack on time_left and pin everything else (map, gold, hp) exactly.
HOST_SEED42 = {"gold": 250, "city_hp": 510, "nonzero": 58}


def summary(state):
    grid = state["map_grid"]
    return {
        "score": state["score"], "gold": state["gold"],
        "dwarves": state["dwarves"], "time_left": state["time_left"],
        "city_hp": state["city_hp"],
        "nonzero": sum(1 for c in grid if c),
        "values": sorted(set(grid)),
        "fp": sum((i + 1) * v for i, v in enumerate(grid)),
    }


class Server:
    """one websocket server per port; hands back the single connection the
    matching world makes."""

    def __init__(self, port):
        self.port = port
        self.ws = None
        self.connected = asyncio.Event()

    async def handler(self, ws):
        self.ws = ws
        self.connected.set()
        try:
            await asyncio.Future()  # keep the connection open
        except asyncio.CancelledError:
            pass

    async def request(self, payload):
        await self.ws.send(json.dumps(payload))
        return json.loads(await self.ws.recv())


async def run(n):
    servers = [Server(BASE_PORT + i) for i in range(n)]
    cms = [websockets.serve(s.handler, "127.0.0.1", s.port, max_size=None)
           for s in servers]
    started = [await cm.__aenter__() for cm in cms]
    try:
        print(f"listening on ports {BASE_PORT}..{BASE_PORT + n - 1}, launching one "
              f"game hosting {n} worlds")
        exe = default_game_exe()
        env = dict(os.environ)
        env["DWARFS_BRIDGE_PORT"] = str(BASE_PORT)
        env["DWARFS_BRIDGE_WORLDS"] = str(n)
        env["DWARFS_BRIDGE_GUI"] = "0"
        game = subprocess.Popen([exe], cwd=os.path.dirname(exe), env=env)

        try:
            await asyncio.wait_for(
                asyncio.gather(*(s.connected.wait() for s in servers)),
                timeout=120)
        except asyncio.TimeoutError:
            up = sum(1 for s in servers if s.connected.is_set())
            print(f"FAIL: only {up}/{n} worlds connected")
            return False
        print(f"all {n} worlds connected\n")

        # seeds: worlds 0 and 1 share seed 42 (the isolation pair), the rest get
        # distinct seeds so the pair has noisy neighbours to be isolated from
        seeds = [42, 42] + [100 + i for i in range(n - 2)]

        firsts = []
        for i, s in enumerate(servers):
            st = await s.request({
                "command": "RESET", "mode": "m5", "difficulty": "Easy",
                "seed": seeds[i], "action_repeat": ACTION_REPEAT})
            firsts.append(summary(st))
            print(f"world {i} (seed {seeds[i]}) reset: {firsts[i]}")
        print()

        ok = True

        # parity: world 0 (seed 42) must match a real single-instance seed-42 game
        p = firsts[0]
        parity = all(p[k] == v for k, v in HOST_SEED42.items()) and \
            1 in p["values"] and 3 in p["values"] and abs(p["time_left"] - 18900) <= 1
        print(f"parity (world0 vs known host seed 42): "
              f"{'ok' if parity else 'MISMATCH'} "
              f"(gold={p['gold']} time_left={p['time_left']} city_hp={p['city_hp']} "
              f"nonzero={p['nonzero']})")
        ok &= parity

        # isolation at reset: the two seed-42 worlds must be byte-identical
        same_reset = firsts[0] == firsts[1]
        print(f"isolation @reset (world0 == world1, same seed): "
              f"{'ok' if same_reset else 'DIFFER'}")
        ok &= same_reset

        # divergence: a different-seed world must differ from world 0
        if n > 2:
            diverged = firsts[2]["fp"] != firsts[0]["fp"]
            print(f"divergence (world2 seed {seeds[2]} != world0): "
                  f"{'ok' if diverged else 'IDENTICAL?!'}")
            ok &= diverged
        print()

        # step every world in lockstep; the seed-42 pair gets the SAME action each
        # step and must stay identical the whole way (isolation under stepping)
        t0 = time.perf_counter()
        drift_step = None
        for step in range(STEPS):
            states = []
            for i, s in enumerate(servers):
                st = await s.request({"command": "STEP", "action": 0})
                states.append(st)
            if drift_step is None and summary(states[0]) != summary(states[1]):
                drift_step = step
            if any(st["terminated"] for st in states):
                print(f"a world terminated at step {step}")
                break
        dt = time.perf_counter() - t0
        total_steps = (step + 1) * n

        pair_ok = drift_step is None
        print(f"isolation under stepping (seed-42 pair identical for {step + 1} "
              f"steps): {'ok' if pair_ok else f'DRIFTED at step {drift_step}'}")
        ok &= pair_ok

        end0, end1 = summary(states[0]), summary(states[1])
        print(f"world0 end: {end0}")
        print(f"world1 end: {end1}")
        print(f"\nthroughput: {total_steps} world-steps in {dt:.2f}s = "
              f"{total_steps / dt:.0f} world-steps/s aggregate "
              f"({(step + 1) / dt:.0f}/s per world x {n})")

        for s in servers:
            try:
                await s.ws.send(json.dumps({"command": "QUIT"}))
            except Exception:
                pass
        print(f"\n{'PASS' if ok else 'FAIL'}")
        return ok
    finally:
        try:
            game.terminate()
        except Exception:
            pass
        for cm in cms:
            await cm.__aexit__(None, None, None)


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 4
    sys.exit(0 if asyncio.run(run(n)) else 1)
