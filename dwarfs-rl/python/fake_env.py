"""fills in for the real gym env so the mod can be tested without it.

runs the same websocket server the env does (localhost:8765), waits for the
mod to connect then drives a real episode, RESET into a 5 minute easy arcade
run and a few thousand STEPs. run this first then start the patched game.
"""

import asyncio
import json
import time

import websockets

STEPS = 3000


def describe(state, label):
    cells = state["map_grid"]
    seen = sorted(set(cells))
    nonzero = sum(1 for c in cells if c)
    print(f"{label}: gold={state.get('gold')} score={state.get('score')} "
          f"dwarves={state.get('dwarves')} time_left={state.get('time_left')} "
          f"city_hp={state.get('city_hp')} tick={state.get('tick')}")
    print(f"   grid: {nonzero}/{len(cells)} nonzero, values: {seen}")


async def reset(ws, **kw):
    await ws.send(json.dumps({"command": "RESET", **kw}))
    return json.loads(await ws.recv())


async def drive(ws):
    print("mod connected, sending RESET (Easy / m5 / seed 42)")
    first = await reset(ws, mode="m5", difficulty="Easy", seed=42)
    describe(first, "reset")

    t0 = time.perf_counter()
    state = None
    rewards = 0.0
    done = 0
    for i in range(STEPS):
        await ws.send(json.dumps({"command": "STEP", "action": i % 3}))
        state = json.loads(await ws.recv())
        rewards += state["immediate_reward"]
        done = i + 1
        if state["terminated"]:
            print(f"episode ended at step {i}")
            break
    dt = time.perf_counter() - t0

    describe(state, f"after {done} steps")
    print(f"reward total: {rewards}")
    print(f"{done} steps in {dt:.2f}s = {done / dt:.0f} steps/s "
          f"({dt / done * 1000:.2f} ms per round trip)")

    expected_drop = first["time_left"] - state["time_left"]
    print(f"timer moved {expected_drop} ticks over {done} steps "
          f"({'ok' if expected_drop > 0 else 'NOT TICKING'})")

    # speed throttle. 60 fps pacing should hold 120 steps to about 2 seconds
    # and dropping the throttle should let it rip again. pacing works headless
    # so keep render off, no need to light the window up just to time it
    await ws.send(json.dumps({"command": "RENDER", "enabled": False, "max_fps": 60}))
    t0 = time.perf_counter()
    for _ in range(120):
        await ws.send(json.dumps({"command": "STEP", "action": 0}))
        state = json.loads(await ws.recv())
    paced = time.perf_counter() - t0
    await ws.send(json.dumps({"command": "RENDER", "enabled": False, "max_fps": 0}))
    t0 = time.perf_counter()
    for _ in range(120):
        await ws.send(json.dumps({"command": "STEP", "action": 0}))
        state = json.loads(await ws.recv())
    free = time.perf_counter() - t0
    print(f"throttle: 120 steps at 60fps took {paced:.2f}s "
          f"({'ok' if paced > 1.6 else 'NOT PACED'}), "
          f"unthrottled took {free:.2f}s ({'ok' if free < paced / 2 else 'STILL SLOW'})")

    # actions. find empty cells near the middle of the window and try to build,
    # walls cost 20 on easy and dynamite 25 so gold dropping is the proof
    W, H = 60, 40
    grid = state["map_grid"]
    empties = [(c, r) for r in range(H) for c in range(W) if grid[r * W + c] == 1]
    # closest to the city first, the starting clearing is the only discovered
    # ground early on. far away "empty" cells are sealed caves you cant touch
    empties.sort(key=lambda p: (p[0] - 30) ** 2 + (p[1] - 20) ** 2)
    print(f"action test: {len(empties)} empty cells to try")

    wall_at = None
    for cx, cy in empties[:100]:
        gold_before = state["gold"]
        await ws.send(json.dumps({"command": "STEP", "action": 2, "x": cx, "y": cy}))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            cell = state["map_grid"][cy * W + cx]
            print(f"wall built at ({cx},{cy}): gold {gold_before}->{state['gold']}, "
                  f"cell now {cell} ({'ok' if cell == 2 else 'UNEXPECTED'})")
            wall_at = (cx, cy)
            break
    if not wall_at:
        print("wall placement: FAILED everywhere")

    boom_at = None
    for cx, cy in empties[:100]:
        if (cx, cy) == wall_at:
            continue
        gold_before = state["gold"]
        await ws.send(json.dumps({"command": "STEP", "action": 1, "x": cx, "y": cy}))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            print(f"dynamite set at ({cx},{cy}): gold {gold_before}->{state['gold']}")
            boom_at = (cx, cy)
            break
    if not boom_at:
        print("dynamite placement: FAILED everywhere")

    # arrows can sit on soil too, green is 5 gold on easy
    arrow_at = None
    for cx, cy in empties[:100]:
        if (cx, cy) in (wall_at, boom_at):
            continue
        gold_before = state["gold"]
        await ws.send(json.dumps({"command": "STEP", "action": 3, "x": cx, "y": cy}))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            print(f"arrow placed at ({cx},{cy}): gold {gold_before}->{state['gold']}")
            arrow_at = (cx, cy)
            break
    if not arrow_at:
        print("arrow placement: FAILED everywhere")

    # episode 2, same seed should give back the same map
    again = await reset(ws, mode="m5", difficulty="Easy", seed=42)
    same = again["map_grid"] == first["map_grid"]
    fresh = again["time_left"] > 18000 and again["gold"] == 250
    print(f"restart: fresh episode {'ok' if fresh else 'BAD'} "
          f"(time_left={again['time_left']} gold={again['gold']}), "
          f"seed 42 reproducible: {'ok' if same else 'MISMATCH'}")

    # different seed should give a different map
    other = await reset(ws, mode="m5", difficulty="Easy", seed=7)
    print(f"seed 7 differs from seed 42: "
          f"{'ok' if other['map_grid'] != first['map_grid'] else 'IDENTICAL?!'}")

    # mode switch, the m15 timer is 56700
    m15 = await reset(ws, mode="m15", difficulty="Easy", seed=42)
    print(f"m15 reset: time_left={m15['time_left']} "
          f"({'ok' if m15['time_left'] > 50000 else 'WRONG MODE'})")

    print("sending QUIT, the game should close itself")
    await ws.send(json.dumps({"command": "QUIT"}))
    try:
        await asyncio.wait_for(ws.recv(), timeout=15)
    except Exception:
        pass  # connection dropping is what we want here
    print("done")
    finished.set()


finished = asyncio.Event()


async def main():
    async with websockets.serve(drive, "127.0.0.1", 8765, max_size=None):
        print("listening on ws://127.0.0.1:8765, start the patched game now")
        await finished.wait()


if __name__ == "__main__":
    asyncio.run(main())
