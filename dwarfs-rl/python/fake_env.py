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
CAM_W, CAM_H = 30, 30


def describe(state, label):
    print(f"{label}: gold={state.get('gold')} score={state.get('score')} "
          f"dwarves={state.get('dwarves')} time_left={state.get('time_left')} "
          f"city_hp={state.get('city_hp')} tick={state.get('tick')}")
    for i in range(4):
        terrain = state.get(f"cam{i}_terrain", [])
        dwarves = state.get(f"cam{i}_dwarves", [])
        enemies = state.get(f"cam{i}_enemies", [])
        nonzero = sum(1 for c in terrain if c)
        dmarks = sum(1 for c in dwarves if c)
        emarks = sum(1 for c in enemies if c)
        print(f"   cam{i}: terrain nonzero {nonzero}/{len(terrain)}, "
              f"dwarves {dmarks} marked, enemies {emarks} marked")
    print(f"   cam_origins={state.get('cam_origins')} "
          f"cave_opened={state.get('cave_opened')} "
          f"instant_seal_delta={state.get('instant_seal_delta')}")


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
        # idle on camera 0, coords 0,0 -- verifies the full roundtrip shape
        await ws.send(json.dumps({"command": "STEP", "action": 0,
                                  "camera": 0, "x": 0, "y": 0}))
        state = json.loads(await ws.recv())
        rewards += state["immediate_reward"]
        done = i + 1
        if state["terminated"]:
            print(f"episode ended at step {i}")
            break
    dt = time.perf_counter() - t0

    describe(state, f"after {done} steps")
    dmarks = sum(1 for c in state.get("cam0_dwarves", []) if c)
    print(f"cam0 dwarf layer: {dmarks} marked vs {state['dwarves']} live dwarves "
          f"({'ok' if dmarks > 0 else 'EMPTY (dwarves may all be south of northmost)'})")
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
        await ws.send(json.dumps({"command": "STEP", "action": 0,
                                  "camera": 0, "x": 0, "y": 0}))
        state = json.loads(await ws.recv())
    paced = time.perf_counter() - t0
    await ws.send(json.dumps({"command": "RENDER", "enabled": False, "max_fps": 0}))
    t0 = time.perf_counter()
    for _ in range(120):
        await ws.send(json.dumps({"command": "STEP", "action": 0,
                                  "camera": 0, "x": 0, "y": 0}))
        state = json.loads(await ws.recv())
    free = time.perf_counter() - t0
    print(f"throttle: 120 steps at 60fps took {paced:.2f}s "
          f"({'ok' if paced > 1.6 else 'NOT PACED'}), "
          f"unthrottled took {free:.2f}s ({'ok' if free < paced / 2 else 'STILL SLOW'})")

    # actions: use camera 0 (northmost digger). find empty cells in its 30x30 view
    # and try to build. walls cost 30 on easy and dynamite 50.
    cam0 = state.get("cam0_terrain", [])
    empties = [(c, r) for r in range(CAM_H) for c in range(CAM_W)
               if cam0[r * CAM_W + c] == 1]
    # prefer cells closer to the camera centre (the tracked dwarf)
    empties.sort(key=lambda p: (p[0] - CAM_W // 2) ** 2 + (p[1] - CAM_H // 2) ** 2)
    print(f"action test (cam0): {len(empties)} empty cells to try")

    def step_action(action, camera, cx, cy):
        return json.dumps({"command": "STEP", "action": action,
                           "camera": camera, "x": cx, "y": cy})

    wall_at = None
    for cx, cy in empties[:100]:
        gold_before = state["gold"]
        await ws.send(step_action(2, 0, cx, cy))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            cam0 = state.get("cam0_terrain", [])
            cell = cam0[cy * CAM_W + cx] if cam0 else -1
            print(f"wall built at cam0 ({cx},{cy}): gold {gold_before}->{state['gold']}, "
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
        await ws.send(step_action(1, 0, cx, cy))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            print(f"dynamite set at cam0 ({cx},{cy}): gold {gold_before}->{state['gold']}")
            boom_at = (cx, cy)
            break
    if not boom_at:
        print("dynamite placement: FAILED everywhere")

    arrow_at = None
    for cx, cy in empties[:100]:
        if (cx, cy) in (wall_at, boom_at):
            continue
        gold_before = state["gold"]
        await ws.send(step_action(3, 0, cx, cy))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            print(f"arrow placed at cam0 ({cx},{cy}): gold {gold_before}->{state['gold']}")
            arrow_at = (cx, cy)
            break
    if not arrow_at:
        print("arrow placement: FAILED everywhere")

    # tower: farm gold then place. costs 250 on easy
    for _ in range(8000):
        if state["gold"] >= 300 or state["terminated"]:
            break
        await ws.send(step_action(0, 0, 0, 0))
        state = json.loads(await ws.recv())
    print(f"new actions: farmed up to gold={state['gold']} (a tower is 250 on easy)")

    cam0 = state.get("cam0_terrain", [])
    empties = [(c, r) for r in range(CAM_H) for c in range(CAM_W)
               if cam0[r * CAM_W + c] == 1]
    empties.sort(key=lambda p: (p[0] - CAM_W // 2) ** 2 + (p[1] - CAM_H // 2) ** 2)
    tower_at = None
    for cx, cy in empties[:150]:
        if (cx, cy) in (wall_at, boom_at, arrow_at):
            continue
        gold_before = state["gold"]
        await ws.send(step_action(7, 0, cx, cy))
        state = json.loads(await ws.recv())
        if state["action_ok"] and state["gold"] < gold_before:
            print(f"tower built at cam0 ({cx},{cy}): gold {gold_before}->{state['gold']}")
            tower_at = (cx, cy)
            break
    if not tower_at:
        print(f"tower placement: none built (gold={state['gold']}, "
              f"needs 250 and a clear 7x7 off the city)")

    if tower_at:
        tx, ty = tower_at
        for code, name in [(9, "toggle digger"), (13, "toggle train"),
                           (10, "spawn warrior"), (11, "recall warriors")]:
            gb = state["gold"]
            await ws.send(step_action(code, 0, tx, ty))
            state = json.loads(await ws.recv())
            print(f"  {name} (action {code}): ok={state['action_ok']} "
                  f"gold {gb}->{state['gold']}")
        for dx, dy in [(0, 0), (3, 0)]:
            nx, ny = tx + dx, ty + dy
            if 0 <= nx < CAM_W and 0 <= ny < CAM_H:
                await ws.send(step_action(12, 0, nx, ny))
                state = json.loads(await ws.recv())
                print(f"  cannon strike (action 12) at cam0 ({nx},{ny}): "
                      f"ok={state['action_ok']}")

    if wall_at:
        await ws.send(step_action(8, 0, wall_at[0], wall_at[1]))
        state = json.loads(await ws.recv())
        print(f"reinforce full wall: ok={state['action_ok']} "
              f"({'refused, good' if not state['action_ok'] else 'UNEXPECTED PASS'})")
    await ws.send(step_action(8, 0, 0, 0))
    state = json.loads(await ws.recv())
    print(f"reinforce empty tile: ok={state['action_ok']} "
          f"({'refused, good' if not state['action_ok'] else 'UNEXPECTED PASS'})")

    # episode 2, same seed should give back the same map (compare cam0 terrain)
    again = await reset(ws, mode="m5", difficulty="Easy", seed=42)
    same = again.get("cam0_terrain") == first.get("cam0_terrain")
    fresh = again["time_left"] > 18000 and again["gold"] == 250
    print(f"restart: fresh episode {'ok' if fresh else 'BAD'} "
          f"(time_left={again['time_left']} gold={again['gold']}), "
          f"seed 42 reproducible: {'ok' if same else 'MISMATCH'}")

    # different seed should give a different map
    other = await reset(ws, mode="m5", difficulty="Easy", seed=7)
    print(f"seed 7 differs from seed 42: "
          f"{'ok' if other.get('cam0_terrain') != first.get('cam0_terrain') else 'IDENTICAL?!'}")

    # mode switch, the m15 timer is 56700
    m15 = await reset(ws, mode="m15", difficulty="Easy", seed=42)
    print(f"m15 reset: time_left={m15['time_left']} "
          f"({'ok' if m15['time_left'] > 50000 else 'WRONG MODE'})")

    # hazard penalty: exercise the reset field and make sure stepping stays
    # healthy with it on. whether a flood actually happens (and the penalty
    # bites) depends on the map, so this reports rather than asserts
    await reset(ws, mode="m5", difficulty="Easy", seed=42,
                action_repeat=8, hazard_penalty=5.0)
    worst = 0.0
    steps = 0
    for _ in range(1200):
        await ws.send(json.dumps({"command": "STEP", "action": 0,
                                  "camera": 0, "x": 0, "y": 0}))
        state = json.loads(await ws.recv())
        worst = min(worst, state["immediate_reward"])
        steps += 1
        if state["terminated"]:
            break
    print(f"hazard penalty: ran {steps} steps with the field on, worst step "
          f"reward {worst:.1f} ({'flood penalized' if worst < -5 else 'no flood this map'})")

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
