"""times one complete 5 minute arcade round at training settings.

headless, action_repeat 8, idle policy. answers "how fast is an episode
really" and proves the terminated flag fires when the round ends.
"""

import asyncio
import json
import time

import websockets

finished = asyncio.Event()


async def drive(ws):
    print("mod connected, full episode, headless, action_repeat=8")
    await ws.send(json.dumps({"command": "RENDER", "enabled": False}))
    await ws.send(json.dumps(
        {"command": "RESET", "mode": "m5", "difficulty": "Easy",
         "seed": 42, "action_repeat": 8}))
    state = json.loads(await ws.recv())
    print(f"start: time_left={state['time_left']} gold={state['gold']}")

    t0 = time.perf_counter()
    steps = 0
    rewards = 0.0
    while not state["terminated"]:
        await ws.send(json.dumps({"command": "STEP", "action": 0}))
        state = json.loads(await ws.recv())
        steps += 1
        rewards += state["immediate_reward"]
        if steps % 500 == 0:
            print(f"  {steps} steps, time_left={state['time_left']}")
        if steps > 5000:
            print("never terminated?!")
            break
    dt = time.perf_counter() - t0

    print(f"episode finished: {steps} decisions ({steps * 8} frames) "
          f"in {dt:.1f}s wall time")
    print(f"final: score={state['score']} gold={state['gold']} "
          f"dwarves={state['dwarves']} time_left={state['time_left']} "
          f"terminated={state['terminated']}")
    print(f"reward total: {rewards} (should be score minus the death penalty "
          f"when the round was lost early)")
    print(f"a 5:15 game round at {315 / dt:.1f}x real speed")

    await ws.send(json.dumps({"command": "QUIT"}))
    try:
        await asyncio.wait_for(ws.recv(), timeout=15)
    except Exception:
        pass
    finished.set()


async def main():
    async with websockets.serve(drive, "127.0.0.1", 8765, max_size=None):
        print("listening on ws://127.0.0.1:8765, start the patched game")
        await finished.wait()


if __name__ == "__main__":
    asyncio.run(main())
