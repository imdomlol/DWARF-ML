# Multi-world (Path C) — many game worlds, one process, one GPU device

Status: **production built and working end-to-end (serial scheduler).** N worlds
run in one process on one device, each deterministic and isolated, reachable from
Python as a normal vec env. Verified at 10 worlds. What remains is a throughput
upgrade (thread the worlds across cores) and combat-regime validation — see §5/§6.
This doc is the single place to understand the multi-world feature: the goal, what
we've learned, what exists today, and what's left. The broader headless
investigation (paths A–F) lives in [HEADLESS.md](HEADLESS.md); this is the deep
dive on Path C.

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
  a headless driver that owns the device, builds N detached trainee worlds sharing
  its infra, and pumps them all in lockstep from its `Update` hook (serial,
  one core). Each world has its own port/socket/episode lifecycle; a detached
  `RESET` rebuilds the level directly (no fade) and each `STEP` hand-drives the
  arcade `Update` sequence.
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
6. ◐ **Robustness (partial).** Per-world logging (one log per port) and graceful
   teardown (Python owns the one process; QUIT parks a world) are in. A throwing
   command is caught in `World.Pump` and the world is parked without taking down
   siblings. Still to harden: a hard crash inside `DriveFrame`, and rebuild-on-
   demand for a parked world.

### Next: throughput (thread the worlds)

The scheduler is **serial** — all N worlds' sim runs on one core, so per-world
throughput falls as N grows (10 worlds ≈ 28 steps/s each, 276/s aggregate at
`action_repeat=4`). This is correct and matches the proven-safe spike, but it
doesn't yet use the CPU-scaling headroom that is the whole point. The throughput
upgrade is to run each world on its own thread (the per-world `Pump`/drive logic is
already factored for it). That depends on **§6 shared-infra thread-safety** holding
under concurrency — verify with a threaded re-run of the isolation test before
trusting it.

---

## 6. Open risks / still to verify

- ✅ **Obs/reward parity** with a real single-instance game — verified. A detached
  seed-42 Easy world matches a normal game's map exactly (same crop, mask, tile
  values, gold, HP). One documented difference: the detached world's first obs
  reports the pristine generated `time_left` (18900) while a normal game reports
  18899 because its menu→game fade burns one frame first. A 1-tick start offset,
  irrelevant to training.
- ✅ **Isolation (no-combat regime)** — verified under stepping at 10 worlds: two
  same-seed worlds stay byte-identical for a full run while eight other seeds run
  alongside; each distinct seed yields a distinct map.
- **Throughput / scaling** — measured serial: 10 worlds ≈ 276 steps/s aggregate
  (~28/s each) at `action_repeat=4`, one core. Threading across cores is the open
  upgrade (§5).
- **Enemy/combat path** under multi-world — still deferred; monster caves are rare,
  so a dwarf reliably digging into one needs a much longer game than the tests run.
  Validate once long runs are cheap. Re-verify isolation with combat active.
- **Shared-infra mutation under concurrency** — the serial scheduler is safe (it's
  what the spike proved). Threading the worlds assumes shared fields (device,
  sound, textures, fonts) are read-only during sim; `GenerateLevel` does call the
  shared `xSoundSystem.StartGame()`, which is harmless for headless correctness but
  is a write to shared state — confirm nothing shared that affects obs/reward is
  written per-world before trusting the threaded driver.

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
