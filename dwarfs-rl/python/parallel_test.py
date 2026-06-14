"""drives several game instances at once, one websocket server per port.

this is the shape the real training will have, N copies of the patched game
each hooked to its own env worker all stepping at the same time. run this
then launch N games with DWARFS_BRIDGE_PORT set to 8765, 8766 and so on.

answers two questions, does steam even let multiple instances run at all
and what combined steps/s we get out of it.
"""

import asyncio
import json
import sys
import time

import websockets

INSTANCES = int(sys.argv[1]) if len(sys.argv) > 1 else 3
BASE_PORT = 8765
STEPS = 300

results = {}
all_done = asyncio.Event()


def make_handler(port):
    async def drive(ws):
        print(f"[{port}] game connected")
        await ws.send(json.dumps({"command": "RENDER", "enabled": False}))
        await ws.send(json.dumps(
            {"command": "RESET", "mode": "m5", "difficulty": "Easy",
             "seed": port, "action_repeat": 8}))
        state = json.loads(await ws.recv())
        print(f"[{port}] reset ok, time_left={state['time_left']}")

        t0 = time.perf_counter()
        steps = 0
        for _ in range(STEPS):
            await ws.send(json.dumps({"command": "STEP", "action": 0}))
            state = json.loads(await ws.recv())
            steps += 1
            if state["terminated"]:
                break
        dt = time.perf_counter() - t0

        print(f"[{port}] {steps} steps ({steps * 8} frames) in {dt:.1f}s, "
              f"score={state['score']}")
        results[port] = (steps, t0, t0 + dt)

        await ws.send(json.dumps({"command": "QUIT"}))
        try:
            await asyncio.wait_for(ws.recv(), timeout=10)
        except Exception:
            pass
        if len(results) == INSTANCES:
            all_done.set()
    return drive


async def main():
    servers = []
    for i in range(INSTANCES):
        port = BASE_PORT + i
        servers.append(await websockets.serve(
            make_handler(port), "127.0.0.1", port, max_size=None))
    print(f"listening on ports {BASE_PORT} to {BASE_PORT + INSTANCES - 1}, "
          f"launch {INSTANCES} games")

    try:
        await asyncio.wait_for(all_done.wait(), timeout=180)
    except asyncio.TimeoutError:
        print(f"TIMEOUT, only {len(results)}/{INSTANCES} instances finished")

    if results:
        total = sum(s for s, _, _ in results.values())
        start = min(t0 for _, t0, _ in results.values())
        end = max(t1 for _, _, t1 in results.values())
        frames = total * 8
        print(f"aggregate: {total} decisions / {frames} frames across "
              f"{len(results)} instances in {end - start:.1f}s "
              f"= {frames / (end - start):.0f} frames/s combined "
              f"({frames / (end - start) / 60:.1f}x real speed)")

    for s in servers:
        s.close()


if __name__ == "__main__":
    asyncio.run(main())
