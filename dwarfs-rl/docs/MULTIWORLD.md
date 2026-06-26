# Multi-world (Path C) — many game worlds, one process, one GPU device

Status: **production built and working end-to-end, threaded.** N worlds run in one
process on one device, each deterministic and isolated, ticking in parallel across
cores, reachable from Python as a normal vec env. Verified at 10 worlds, including
worlds dying mid-episode. What remains is combat-regime validation and longer-run
scaling — see §6. This doc is the single place to understand the multi-world
feature: the goal, what we've learned, what exists today, and what's left. The
broader headless investigation (paths A–F) lives in [HEADLESS.md](HEADLESS.md);
this is the deep dive on Path C.

---

## 1. The goal

Train across many game instances in parallel **without being capped by the
graphics card.**

Today each training environment is its own `Dwarfs.exe` process, and each process
holds its own Direct3D 9 device. The GPU/driver only hands out a few concurrent
D3D9 devices before failing with *"No suitable graphics card found"* — on this
machine (AMD RX 5700) that ceiling is ~2–3 instances; a friend's RTX 5070 manages
~6–7. The ceiling is the **device**, not CPU or RAM.

The multi-world feature runs **M independent game worlds inside a single process,
sharing one real GPU device.** The number of parallel training environments then
scales on CPU/RAM, not the GPU — the device stops being a scaling axis.

**Definition of done:** N ≫ 2 worlds in one process, each stepping
deterministically over a full episode and returning obs/reward identical to a
normal single-instance game, at acceptable per-world throughput (lower per world
is fine — we have many).

---

## 2. What we discovered (all validated with running code)

We decompiled `Dwarfs.exe` / `DataTypes.dll` (ilspycmd) and ran an in-process
spike ([mod/MultiWorldSpike.cs](../mod/MultiWorldSpike.cs)) that builds extra
`Game1` worlds from inside the running game. Findings:

| Question | Result |
|---|---|
| Is `Game1`/the sim static-heavy? | **No.** `Game1` has zero static mutable state (only 4 pure static utility methods); the whole game has only static tower-cost constants + 2 enemy-ID counters. `DataTypes.dll` has no static fields. **A world is a `Game1` instance.** |
| Can >1 `Game1` exist in one process? | **Yes.** A second `Game1` constructs cleanly and the primary keeps running. XNA does not forbid it. |
| Does the simulation need the GPU? | **No.** `UpdateGamefield` (the 2,295-line core sim) has **0** GraphicsDevice/Texture references; `GenerateLevel` is data-only bar an optional tips-draw. All ~2000 GPU refs are in `Draw` (suppressed headless) and content load. |
| What's the only device dependency? | **Content load at bring-up.** A fresh `Game1.Initialize()` dies on `Content.Load<SpriteFont>` ("GraphicsDevice component not found"). Nothing else in the sim path needs the device. |
| Can a world simulate device-free to episode end? | **Yes.** A world built without `Initialize` (sharing the host's already-loaded content) ran ~15k frames to the game's own end state, no crash. |
| Can two worlds run **isolated**? | **Yes, proven.** See below. |
| Is the sim deterministic per seed? | **Yes** (falls out of the isolation proof). |

### The isolation proof

Build two worlds with different seeds and run them interleaved; then run one seed
**solo** and the **same seed paired** with a different-seed world:

```
SOLO  (seed 111, alone)  -> score=1040 dwarves=4 gold=1105 timeLeft=12900 mapFP=274683
A     (seed 111, paired) -> score=1040 dwarves=4 gold=1105 timeLeft=12900 mapFP=274683
B     (seed 222, paired) -> score=215  dwarves=4 gold=293  timeLeft=12900 mapFP=238259
```

World A (run alongside B) is **byte-identical** to the same seed run solo — score,
dwarves, gold, *and* map fingerprint all match. The presence of the second world
had **zero** effect on the first → no cross-contamination, and full per-seed
determinism (which RL needs anyway). A vs B (different seeds) diverge cleanly.

**Bottom line: every "will this even work?" unknown is retired.** What remains is
engineering.

---

## 3. The architecture (the validated approach)

- **One host `Game1`** boots normally via XNA `Game.Run()` — it owns the single
  real GPU device and the one-time-loaded content (textures, fonts, sound).
- **Each extra world** is a `Game1` that is constructed but **not** run through
  `Initialize`/`LoadContent`. Instead it:
  1. **clones the host's fields** → shares the device, sprite batch, sound,
     textures, fonts (read-only infrastructure);
  2. **reallocates its own per-world state** → a fresh `xGameMap`, fresh copies of
     every `List<>` field (the ~25 entity lists), fresh
     `xDifficulty`/`xTowerDefense`/`xPlayerInteraction`/`randomizer`;
  3. lets the game's own `SetDifficulty` → `ClearGame` → `GenerateLevel` build the
     level (these rebuild `resources`/`xCity`/buildings and the map).
- **Per frame**, each world ticks the real arcade `Update` sequence:
  `resources.CheckTheAccount` → `UpdateGamefield` → `NonSpeedEvents` →
  `UpdateDynamicMaps` → `Update_CampaignQuests` → `resources.BalanceTheAccount`.
- **Each world has its own lockstep mailbox + port**, so one Python env worker
  drives each world independently, exactly as it drives a process today.

Why this shape: the sim is device-free but fused into the `Game1` class, so a
world *is* a `Game1`. We can't avoid a `Game1` per world, but we *can* avoid a
device per world — only the host pays the device/content cost, once.

---

## 4. What's already implemented

**The production feature (built, working):**

- **Per-world `Bridge`** — [mod/World.cs](../mod/World.cs) holds all the
  lockstep/episode/reward/socket state that used to be static on `Bridge`, keyed
  per `Game1`. [Bridge.cs](../mod/Bridge.cs) is now the coordinator (registry +
  the five woven hooks + the host-level device/window concerns).
- **Detached worlds + host loop** — [mod/WorldSim.cs](../mod/WorldSim.cs) promotes
  the spike's build pattern (`CreateDetached` / `BuildLevel` / `DriveFrame`) into
  reusable production code. With `DWARFS_BRIDGE_WORLDS=N` the real `Game1` becomes
  a headless driver that owns the device and builds N detached trainee worlds
  sharing its infra. Each world has its own port/socket/episode lifecycle; a
  detached `RESET` rebuilds the level directly (no fade) and each `STEP` hand-drives
  the arcade `Update` sequence. `CreateDetached` reallocates **only** the per-world
  entity lists (the ones `ClearGame` clears); other `List<>` fields are shared
  read-only content (death-tip textures etc.) — blanket-reallocating those to empty
  was what crashed the game-over path.
- **Threaded driver (default)** — each world runs `World.RunLoop` on its own
  thread, so the worlds tick in parallel across cores; the host stays at fixed-step
  so it yields cores. `DWARFS_BRIDGE_SERIAL=1` falls back to the one-thread serial
  scheduler. Threading required making the obs path thread-safe: the crop origin
  now flows by return value/params instead of a shared `GameState` static, and all
  reflection caches are pre-bound on the host thread before workers start.
  Measured ~1.7× serial at 10 worlds under heavy sim (`action_repeat=32`); the gap
  widens with heavier sim and with real multi-process training (where Python isn't
  a single-event-loop bottleneck).
- **Error isolation** — a STEP whose sim throws ends that episode with a terminated
  reply instead of hanging the env; a world crashing is caught and parked without
  taking down its siblings or the host.
- **Python M-ports** — [python/dwarfs_env.py](../python/dwarfs_env.py)
  `make_world_env(n)` launches **one** game hosting N worlds and N workers
  connecting to ports `8765..8765+N-1`; [python/train.py](../python/train.py)
  exposes it as `--multiworld`.
- **Correctness gate** — [python/multiworld_test.py](../python/multiworld_test.py)
  proves isolation (two same-seed worlds stay byte-identical for a whole run while
  siblings run other seeds), divergence, parity with a normal single-instance game
  (modulo a documented 1-tick start offset), and throughput. **Passes at 10
  worlds.** Single-instance `fake_env.py` still passes unchanged.

**Validation spike (superseded):**

- [mod/MultiWorldSpike.cs](../mod/MultiWorldSpike.cs), gated
  `DWARFS_BRIDGE_C_SPIKE=1`, still runs once from the primary's `Update` hook in
  single-instance mode. It proved the build pattern; the production code above now
  carries it. Safe to retire in a later cleanup.

**Reusable foundations already in the codebase (the build leans on these):**

- The mod's reflection helpers — [GameState.cs](../mod/GameState.cs),
  [GameControl.cs](../mod/GameControl.cs), [GameAction.cs](../mod/GameAction.cs) —
  already operate on a **passed `game` instance**, so they are multi-world ready
  with no change.
- The loader's `Boot` weave + `BeforeUpdate` / `BeforeGenerateLevel` /
  `ShouldRender` / `InputGate` hooks ([loader/Program.cs](../loader/Program.cs)).
- The env's per-port launch model —
  [python/dwarfs_env.py](../python/dwarfs_env.py) `DwarfsEnv(game_exe=…,
  DWARFS_BRIDGE_PORT=…)` and `make_vec_env`, which already runs one worker per
  port.

**Complementary, but a separate feature** (branch `headless-probe`): the
probe-and-cap guardrail — `python/headless_probe.py` + `--power max-safe` — which
caps a *multi-process* run to the measured device ceiling. Multi-world supersedes
the need for it, but it stays as a robust fallback.

---

## 5. Build status

Items 1–5 are **done** (see §4); 6 is partial. What's left is throughput and
hardening, not "will it work."

1. ✅ **Bring-up host.** The real `Game1` is a dedicated headless driver (owns the
   device, plays nothing); it builds N detached trainee worlds. (Single-instance
   keeps the old host-is-trainee path unchanged.)
2. ✅ **Per-world `Bridge`.** Done — `World` per `Game1`, `Bridge` coordinates.
3. ✅ **Multi-world host loop.** Serial scheduler: the driver pumps every trainee
   once per frame; each world advances only on its own STEP and replies with its
   own obs/reward. Per-world RESET rebuilds the level directly; per-world QUIT
   parks just that world (the shared process stays up for its siblings).
4. ✅ **Correctness check.** `multiworld_test.py` — isolation/divergence/parity all
   pass at 10 worlds; single-instance `fake_env.py` unchanged.
5. ✅ **Python side.** `make_world_env` + `--multiworld`.
6. ✅ **Robustness.** Per-world logging (one log per port), graceful teardown
   (Python owns the one process; QUIT parks a world), error isolation (a throwing
   STEP ends the episode with a reply; a crash is caught and the world parked
   without touching siblings). Open: rebuild-on-demand for a parked world.

### Throughput (threaded) — done

Each world runs on its own thread (`World.RunLoop`); the host stays at fixed-step so
it yields cores. `DWARFS_BRIDGE_SERIAL=1` forces the old serial scheduler. Level
generation is serialized behind a lock (`WorldSim.genLock`) because `GenerateLevel`
touches shared infra — without it, worlds resetting concurrently (exactly what a vec
env does at startup) corrupt each other; per-frame stepping stays fully parallel.

Measured with `python/bench_throughput.py` at the training default
`action_repeat=8` (env-collection throughput, no PPO):

| worlds | aggregate env-steps/s | per-world | vs 1 |
|---|---|---|---|
| 1 (single-instance) | ~150–185 | ~150–185 | 1× |
| 4 (multi-world)     | ~815 | ~204 | ~5× |
| 10 (multi-world)    | ~970 | ~97 | ~6× |

So ~5–6× more experience per wall-second at 4–10 worlds in one process on one GPU
device. The per-world rate falls with count (CPU contention + the bench's single
asyncio driver, which real `SubprocVecEnv` avoids by giving each worker its own
process) — so real training should scale at least this well, often better. Under
heavier per-frame sim the threaded driver beats serial ~1.7× (`action_repeat=32`:
~21k vs ~12.5k sim-frames/s). Determinism holds: same-seed worlds die at the
identical step whether serial or threaded.

---

## 6. Open risks / still to verify

- ✅ **Obs/reward parity** with a real single-instance game — verified. A detached
  seed-42 Easy world matches a normal game's map exactly (same crop, mask, tile
  values, gold, HP). One documented difference: the detached world's first obs
  reports the pristine generated `time_left` (18900) while a normal game reports
  18899 because its menu→game fade burns one frame first. A 1-tick start offset,
  irrelevant to training.
- ✅ **Isolation (no-combat regime), serial AND threaded** — verified under
  stepping at 10 worlds, worlds ticking truly in parallel: two same-seed worlds stay
  byte-identical for a full run (and die at the identical step) while eight other
  seeds run alongside; each distinct seed yields a distinct map.
- ✅ **Shared-infra mutation under concurrency** — the obs path's one shared mutable
  (the crop static) is gone, and reflection caches are pre-bound off-thread; the
  per-world entity lists are isolated while shared content is read-only. The
  threaded isolation test passing is the evidence nothing obs/reward-affecting is
  written across worlds. (`GenerateLevel` still calls shared `xSoundSystem` — audio
  only, doesn't touch obs/reward.)
- **Throughput / scaling** — threaded ~1.7× serial at 10 worlds under heavy sim on
  this machine; not yet profiled for the core-count ceiling or for the harder
  difficulties' 1000² maps. Longer-run and many-more-worlds scaling unmeasured.
- **Enemy/combat path** under multi-world — still deferred; monster caves are rare,
  so a dwarf reliably digging into one needs a much longer game than the tests run.
  Note a city *death* (flood) now exercises the game-over path correctly; full
  combat (enemies spawning, the 2 static enemy-ID counters) is still unvalidated
  under concurrency. Re-verify isolation with combat active.

---

## 7. File map

- [docs/HEADLESS.md](HEADLESS.md) — the full headless investigation (paths A–F);
  the Path C section carries the spike evidence and recalibrated status.
- [docs/PROTOCOL.md](PROTOCOL.md) — the wire contract (source of truth).
- [mod/MultiWorldSpike.cs](../mod/MultiWorldSpike.cs) — the validation spike.
- [mod/Bridge.cs](../mod/Bridge.cs) — the mod core (to be made per-world).
- [mod/GameState.cs](../mod/GameState.cs) / [GameControl.cs](../mod/GameControl.cs)
  / [GameAction.cs](../mod/GameAction.cs) — reflection helpers (already per-game).
- [python/dwarfs_env.py](../python/dwarfs_env.py),
  [python/train.py](../python/train.py),
  [python/headless_probe.py](../python/headless_probe.py).

---

*Branches: `path-c-multiworld` holds the spike + this doc; `headless-probe` holds
the probe-and-cap guardrail (Path F). Both branch off `main`-line work.*
