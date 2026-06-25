# Multi-world (Path C) — many game worlds, one process, one GPU device

Status: **foundation proven, production not yet built.** This doc is the single
place to understand the multi-world feature: the goal, what we've learned, what
exists today, and what's left to ship it. The broader headless investigation
(paths A–F) lives in [HEADLESS.md](HEADLESS.md); this is the deep dive on Path C.

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

**Toward this feature specifically:**

- **Validation spike** — [mod/MultiWorldSpike.cs](../mod/MultiWorldSpike.cs),
  gated `DWARFS_BRIDGE_C_SPIKE=1`, run once from the primary's `Update` hook. This
  is **throwaway proof-of-concept**, not production: it constructs extra worlds,
  proves the build pattern, isolation, determinism, and economy. It will be
  *replaced* by the production host, not shipped.

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

## 5. What's left to implement (production build)

No remaining unknowns of the "will it work" kind — this is engineering.

1. **Bring-up host.** Promote the spike's build pattern into the real boot path:
   the host `Game1` initializes normally and owns the device; M-1 extra worlds are
   constructed and seeded with the shared infra + fresh per-world state. Decide who
   the host is (a dedicated world, or world 0 doubling as a trainee).

2. **Per-world `Bridge`.** Today [Bridge.cs](../mod/Bridge.cs) keeps `phase`,
   `frame`, the single-slot mailbox, and reward/episode state in **static** fields
   (one game assumed). Refactor these into a per-world object keyed by the `Game1`
   instance, so each world has its own lockstep state, socket/port, and episode
   lifecycle. This is the load-bearing change; everything else hangs off it.

3. **Multi-world host loop.** Drive the M worlds' per-frame sim in lockstep — each
   world advances only when its env worker sends a STEP, and replies with its own
   obs/reward. Handle per-world RESET (rebuild that world via
   `ClearGame`/`GenerateLevel`) and per-world QUIT/shutdown.

4. **Correctness check.** Wire one multi-world `Game1` to the real `Bridge` and run
   `python/fake_env.py` (the full protocol regression) against it — confirm
   obs/reward match a normal single-instance game **before** trusting it for
   training. This is the gate between "the spike says it works" and "we believe
   the numbers."

5. **Python side.** Expose M ports from one process; point `make_vec_env` at the
   single process with M workers. The env code barely changes (it already connects
   per port) — mostly it's launching one game instead of N.

6. **Robustness.** Error isolation (one world crashing must not take down the
   others or the host), per-world logging, and graceful teardown.

---

## 6. Open risks / still to verify

- **Obs/reward parity** with a real single-instance game — not yet compared
  (item 4 above closes this).
- **Enemy/combat path** under multi-world — deferred by choice; monster caves are
  rare, so a dwarf reliably digging into one needs a much longer game than a
  spike. Validate once long runs are cheap.
- **Throughput / scaling** — how many worlds before CPU-bound? Unmeasured.
  Per-world cost is sim + reflection overhead; the device cost is paid once.
- **Isolation completeness** — the solo-vs-paired proof is strong but covers the
  no-combat regime. Re-verify once combat is active and when many worlds run.
- **Shared-infra mutation** — the pattern assumes shared fields (device, sound,
  textures, fonts) are read-only during sim. Confirm nothing shared is *written*
  per-world (e.g. a sound trigger or texture-handler cache); anything that is must
  move to per-world.

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
