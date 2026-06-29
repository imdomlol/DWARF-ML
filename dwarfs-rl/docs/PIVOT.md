# PIVOT — from 4 cameras to all-dwarves entity control

Status: **proposed design, not yet built** (2026-06-28). This is the agreed plan
for replacing the camera-based observation/action with a per-dwarf entity model.
`PROTOCOL.md` is still the wire contract and must be updated *with* the mod/env
when this lands — this doc is the design, not the contract.

---

## 1. The idea in one paragraph

Train **one shared policy** across **multiple parallel game worlds** (multi-world
and/or multi-process, unchanged). Inside each world, every tick the model observes
**all dwarves at once** — each dwarf as a small feature vector (where it is, where
its nearest cave/ore/spawner/hazard is, whether it already has a command running) —
plus the **global game stats we already send** (gold, time, city HP, build costs).
It then outputs **one action per dwarf simultaneously** (a small semantic choice:
leave it / send toward cave / send toward ore / return / dynamite), where "send
toward X" is translated by the mod into the game's own **gold-paid arrow** that
steers that dwarf over many seconds. The camera tensors go away entirely.

Why this shape (the reasoning is settled — see the design thread):
- **Credit assignment works**: the action is the *joint* decision over all dwarves,
  so the global reward matches the full decision. The cycling/1-dwarf-per-tick
  alternative was rejected — it costs ~K× the sim time for the same coverage, makes
  the observation incoherent across ticks (wrecks the critic), and still drowns the
  one action in the K−1 dwarves it didn't touch.
- **Parameter sharing**: every dwarf is a training sample for the same per-entity
  policy, so one world yields ~64 decision-samples per tick, not one.
- **The arrow is a built-in macro-action (an "option")**: one gold-paid decision the
  engine executes over many frames → fewer, higher-leverage choices, cleaner credit.
- **It's cheap**: the net shrinks from a CNN over 4×(3,30,30) images to a small MLP
  over a few hundred floats; the obs payload shrinks ~17×. Parallel worlds still
  matter (the **game sim** is the bottleneck, not the obs), and matter *more* here
  because the shared policy and rare events (e.g. lava breaches) are hungrier for
  diverse parallel experience.

---

## 2. What changes vs. what stays

| | Today | After pivot |
|---|---|---|
| Observation | 4 cameras `cam0..3`, each `(3,30,30)` + `stats(9)` | `global(9)` + `dwarves(K, F)` per-dwarf feature matrix |
| What picks the dwarves | northmost/eastmost/southmost/westmost digger | **all** diggers, fixed **K** slots, padded + masked |
| Action | `[action, camera, x, y]` (camera-relative tile) | per-dwarf semantic head ×K + a small global build head |
| "Aim" coordinate frame | tile inside a 30×30 camera crop | **none** — actions reference the dwarf and its known targets |
| Reward | global score-delta − penalties | same **+ per-dwarf event attribution + distance shaping** |
| Policy | `PPO` + `MultiInputPolicy` (CNN) | `MaskablePPO` + `MultiInputPolicy` (MLP/optional set-net) |
| Env API | Gymnasium | **Gymnasium (unchanged)** |
| Parallelism | multi-world / SubprocVecEnv | **unchanged** (and more valuable) |
| Reflection masking of sealed caves | blanks cave tiles to soil | **moot** — no terrain grid is sent anymore |

Removed entirely: `cam{i}_terrain/dwarves/enemies`, `CAM_W/CAM_H`,
`FindExtremeDiggers`, `ReadGridAt`/the crop-origin plumbing. The fog-of-war masking
bug we found is *designed out*, not patched — there is no grid left to mask.

Enemies/combat are **dropped from the obs for now** (decided earlier) — this is a
dig-and-economy agent first; a nearest-enemy per-dwarf feature can be added later
in the same slot format.

---

## 3. The new observation

### Global block (kept, ~unchanged from today's `stats`)
`gold, total_dwarves, time_left, city_hp, cost_wall, cost_dynamite, cost_arrow,
cost_tower, cost_warrior` — the agent can't make economic choices without these and
they aren't per-dwarf, so they stay as one global vector.

### Per-dwarf block — `dwarves` shape `(K, F)`
One row per dwarf slot, `K` fixed (start **K = 64**), padded and masked. Proposed
features `F` per dwarf (~14 floats):

| field | size | encoding |
|---|---|---|
| present | 1 | 1 if this slot holds a live digger, else 0 and the row is zeroed |
| position | 2 | `(x,y)` normalized by map size (so the agent knows global location/edges) |
| → nearest undiscovered cave | 3 | relative `(dx,dy)` normalized + `has_cave` flag |
| → nearest ore | 3 | relative `(dx,dy)` + `has_ore` flag |
| → nearest **discovered** lava/water | 3 | relative `(dx,dy)` + scalar distance (the flood-response signal) |
| distance to nearest spawner | 1 | scalar — "town spacing" signal (nearest of **city + outposts**) |
| diggers within 30 tiles | 1 | count — local crowding |
| arrow active + progress | 2 | `has_active_arrow` flag + fraction of the way to its target |

Two decisions baked in: **targets are relative to their dwarf** (translation-
invariant, and it's what makes the arrow actions work), while the **dwarf's own
position is absolute-normalized**. And **`arrow active + progress` is mandatory,
not optional** — it's what lets the critic value an in-flight commitment *up front*
(paying credit before the dwarf arrives) and stops the policy re-issuing arrows and
re-paying gold. See §6.

### Slot assignment (must be stable)
A slot must map to the **same dwarf across ticks**, or the per-row policy can't
track anyone. Assign by a persistent key (dwarf identity / spawn order). When there
are **> K dwarves**, choose which ones fill the slots by the priority we discussed:
prefer diggers **far from a spawner** (the ones whose decisions matter), then by
age. This is the *only* surviving use of the old "cycling" idea — as a ranking for
slot occupancy, never as a per-tick rotation of the observation.

---

## 4. The new action

### Per-dwarf head (one small categorical per slot, ×K)
Proposed action set — the mod translates each into a concrete game command:

| code | meaning | mod translation |
|---|---|---|
| 0 | **continue / leave it** (no gold) — the dominant no-op | nothing |
| 1 | send → nearest undiscovered cave | place gold-paid arrow toward the cave vector |
| 2 | send → nearest ore | arrow toward the ore vector |
| 3 | send → nearest spawner/city (return) | arrow toward the spawner |
| 4 | **dynamite at this dwarf's tile** | dynamite (the lava-seal reflex) |
| (5) | cancel active arrow | clear the dwarf's command |

This is the realization of "anchor actions to dwarves": no per-dwarf x/y tile
picking, because the targets are already in the observation. Most ticks, most
slots should choose `0` — a confident no-op contributes ~zero gradient and ~zero
PPO-ratio variance, so the effective number of live decisions per tick is small
even with 64 slots.

### Global build head (separate, **open design point**)
Tower / outpost / wall placement is **not** per-dwarf — it needs a map target, and
the per-dwarf model deliberately has no tile coordinate frame. Options to resolve
when we build it: (a) a small global head `build_type` + a **coarse global grid**
target, or (b) **anchor builds to a dwarf** ("build outpost at dwarf N's tile").
Flagged here so it isn't silently dropped; the first iteration can ship per-dwarf
steering only and add building second.

### Masking (needs `MaskablePPO`)
Per slot, mask illegal choices each step: absent slot → only `0`; no known cave →
mask `1`; `gold < cost_arrow` → mask all arrow actions; already has an active arrow
→ mask re-issue (or allow only `cancel`). Masking is the one real algorithm change
(see §7). The *first* iteration can skip it: pad absent dwarves, set `present=0`,
and let the policy learn to no-op them — then add masking if illegal actions slow
learning.

---

## 5. The reward

Keep the existing global shape (`immediate_reward = score delta`, minus
`death_penalty`, minus `invalid_action`), and add two things that make the
joint/delayed structure learnable:

1. **Per-dwarf / per-event attribution.** Route event rewards to the *slot that
   caused them*, not the global pot. The flagship case: a dwarf breaches a **lava**
   cave and it gets sealed in time → credit *that dwarf's slot*. We can detect this
   precisely from data we already surface — `cave_opened == lava-type`, `cave_x/y`
   (the breach tile → the dwarf standing there), and the `instant_seal_delta`
   achievement signal. This collapses the "which of 64 actions earned it?" problem
   to a trivial routing.
2. **Potential-based distance shaping.** Dense breadcrumb for "moving toward the
   thing": `F = γ·Φ(s′) − Φ(s)` with `Φ = −distance_to_target`. Provably policy-
   invariant (Ng 1999), and we already compute the distances — so an arrow toward a
   cave feels good *every step of the journey*, not only as one lump on arrival.

Plus high `γ` (≈0.99–0.995) and GAE (`λ`≈0.95) for the long arrow horizons.

---

## 6. Delayed reward — why it's fine

An arrow's payoff (ore mined, cave sealed) lands many frames after the decision.
That's handled, *provided the in-flight commitment is observable* (it is, via
`arrow active + progress` in §3): the critic learns that "this dwarf is en route to
a cave" is a high-value state, so the **advantage flows to the arrow action
immediately**, before the dwarf arrives. The macro nature *reduces* the credit
burden (fewer decisions competing per unit of game time). The one number worth
measuring to set `γ` is the arrow traversal length: `traversal_frames /
action_repeat` steps — derive it from the dwarf move-speed fields (`m_fMoveSpeed*`,
`m_fMoveSpeedArrow`).

---

## 7. Python / policy side (Gymnasium stays)

Three independent layers; only the inner two move:
- **Gymnasium env** (`DwarfsEnv`): unchanged contract. Redefine `observation_space`
  as a `Dict({"global": Box(9), "dwarves": Box(K,F)})` and `action_space` as
  `MultiDiscrete([per_dwarf_actions]*K)` (+ the global build head). Add an
  `action_masks()` method when masking lands. Websocket bridge, multi-world,
  `VecMonitor`/`SubprocVecEnv` all unchanged.
- **Algorithm**: `PPO` → `MaskablePPO` from **`sb3-contrib`** (same API, still eats a
  Gymnasium env) once masking is wanted. First iteration can stay on vanilla `PPO`.
- **Policy**: still `"MultiInputPolicy"` (it handles the Dict obs). Optionally plug a
  **custom set/attention features extractor** later via
  `policy_kwargs={"features_extractor_class": ...}` to ingest the variable dwarf set
  permutation-invariantly — not required to start.

Add `sb3-contrib` to `requirements.txt` when the masking step lands.

---

## 8. Mod side (grounded in the decompile)

The data all exists and most of it is already bound. Reflection sources:

- **Dwarves** — `lDwarf` (already bound) of `Dwarf`. Per dwarf: `m_v2Position`
  (location), `m_sClass` (already bound; digger = not `"Warrior"`),
  `m_iHomeBase`/`m_iTargetBase` (spawner association), **`m_lv2TargetSquare`** (the
  active arrow target path — non-empty ⇒ has an active command; this is the
  `arrow active`/progress source), and `m_fMoveSpeed*`/`m_fMoveSpeedArrow` (speed,
  for the `γ` sizing in §6).
- **Caves** — `lCave` (List<`Cave`>, populated at level-gen). Per cave: `m_v2Origin`,
  `m_lDarkSquares` (sealed tiles, **cleared on open** ⇒ undiscovered ⇔
  `m_lDarkSquares.Count > 0`), `m_iType` (contents — kept hidden: −1 empty, 0 water,
  1 lava, 2 goblin, 3 treasure, 4 minions, 5 lich, 6 spiderqueen). Nearest
  undiscovered cave = nearest origin/dark-square among unopened caves. (Equivalent
  per-tile signal: `abyGameMapOverlay[x,y] = caveIndex+1` on sealed tiles, 0 on
  discovered.)
- **Ore** — `abyGameMapMinerals` (byte[,]); nearest nonzero (unmasked, already
  human-visible).
- **Discovered lava/water** — `abyGameMapLava` / `abyGameMapWater` (ushort[,]);
  nearest active cell.
- **Spawners** — `lOutposts` (the digger-spawning towers, already referenced in
  `GameAction.cs`) **+ `xCity`** (the main base). Distance to nearest of these.

Replace `GameState.FindExtremeDiggers` with an enumerate-all-diggers builder that
emits the per-dwarf rows, and add per-dwarf action application (arrow placement via
the game's own arrow handler; dynamite via the existing dynamite path).

**Keep feature construction cheap (hard requirement).** With K up to 64, do **not**
scan the whole map per dwarf for nearest ore/cave — that's 64 full-map scans per
world per step and it would steal CPU from the sim and cut how many worlds you can
run. Maintain small lists (ore cells, unopened caves, spawners, active liquid) or a
coarse spatial grid and do O(few) nearest-neighbour lookups. Done this way the obs
cost is negligible and multi-world scaling is fully preserved.

---

## 9. Parallelism (unchanged, still the throughput lever)

Multi-world / multi-process stays exactly as-is — the **sim is the bottleneck**, so
parallel worlds are how you buy experience, and this redesign is near-free per
world (smaller net, smaller payload). If anything, dropping the CNN may let you run
slightly more worlds. The per-dwarf shared policy and rare events make parallel
diversity *more* valuable, not less.

---

## 10. Open questions / risks

- **Global build head** target encoding (coarse grid vs dwarf-anchored) — §4.
- **K = 64** vs smaller: too-large K wastes rows and adds PPO-ratio dimensions; too
  small clips the dwarf count. Tune; the dominant no-op keeps large K cheap.
- **Slot-assignment stability** under spawn/death churn — verify a slot tracks one
  dwarf across ticks before trusting the gradient.
- **Per-dwarf attribution scope** — start with the lava-seal event; decide later
  whether to attribute ore/gold pickups per-dwarf too (cleaner but more plumbing).
  The heavyweight option (COMA / difference rewards) is almost certainly overkill.
- **Combat** still deferred (and now also absent from the obs) — revisit when
  enemies re-enter the picture.
- **Protocol coupling** — the schema change must land in `mod/World.cs`
  (`BuildState`/`HandleCommand`), `python/dwarfs_env.py`, and `docs/PROTOCOL.md`
  **together**, per the repo rule.

---

## 11. Phased build plan

1. **Spec + protocol.** Freeze the per-dwarf `F` layout, the action set, and the
   global block; write the new schema into `PROTOCOL.md`.
2. **Mod obs.** Replace `FindExtremeDiggers` with the all-diggers row builder
   (cheap nearest-neighbour via maintained lists). Emit `global` + `dwarves(K,F)`.
3. **Mod actions.** Per-dwarf semantic action → arrow/dynamite via the game's own
   handlers; refused → `action_ok` per slot.
4. **Env.** New `observation_space`/`action_space`; unpack rows; keep the bridge,
   multi-world, monitors.
5. **Reward.** Add lava-seal attribution + potential-based distance shaping; set
   `γ`/`λ`; measure arrow traversal to confirm `γ`.
6. **Train, vanilla.** Plain `PPO` + `MultiInputPolicy`, `present` flag, **no
   masking** — get end-to-end learning first.
7. **Add masking.** Switch to `MaskablePPO` + `action_masks()` if illegal actions
   prove to slow learning.
8. **(Later) set/attention extractor** and the **global build head**.

Correctness gates to reuse: `fake_env.py` (protocol regression) and
`multiworld_test.py` (isolation/parity) must both pass against the new schema before
trusting a long run.
