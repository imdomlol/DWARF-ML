> **Active plan: [dwarfs-rl/docs/PIVOT.md](dwarfs-rl/docs/PIVOT.md).** The
> observation/action layer is being redesigned into a per-dwarf entity model
> (observe + command all dwarves at once; arrows instead of camera tile-picking).
> The "Action Space TODO" below describes the **current, camera-based** action set
> that ships today; the pivot replaces its *shape* (not the underlying game actions,
> which stay — dynamite, wall, tower, arrow, etc. are reused). Treat PIVOT.md as the
> forward plan and this list as the record of what's wired today.

# Action Space TODO

Actions to expose to the model so it can learn to play. Each unchecked box is an
action still to be wired through the mod's `ApplyAction` and the env's action space.

- [x] **0. Do nothing**
- [ ] **1. Speed up time** *(most important)* — not an agent action. The sim is
  frozen between commands and runs flat out when one arrives, so wall-clock speed
  is set by the trainer, not the policy. The lever is `action_repeat` on RESET
  (1-240 frames per STEP). Left unchecked pending a chat about what you meant.
- [x] **2. Place dynamite**
- [x] **3. Place tower** — action 7
- [x] **4. Place wall**
- [x] **5. Reinforce wall** — action 8. Heads up: the base game has no reinforce,
  walls just have health. So this is defined as paying the wall cost to patch a
  damaged wall back to full HP. Refused if there's no wall there or it's already
  full. Say the word if you'd rather it meant Solidify (turn soil into permanent
  rock) instead, that one's a real handler too.
- [x] **6. Tower actions** — the tile picks which tower (its 3x3 footprint)
  - [x] 6a. Toggle digger dwarf spawner — action 9
  - [x] 6b. Spawn warrior dwarf — action 10
  - [x] 6c. Recall all warrior dwarfs — action 11
  - [x] 6d. Cannon dwarfs — action 12, here the tile is the target to fire at
  - [x] 6e. Toggle train (xp) existing warrior dwarfs — action 13
- [x] **7. Arrow actions** — the four directional arrows are wired (actions 3-6)
  and minerals already show up in the observation (tile code 4) so the model can
  aim at those today. The targeting help you wanted is now unblocked: the
  4-camera obs redesign added a **dwarves layer** (1 digger, 2 warrior) per
  camera, so dwarf positions are in the observation and an "arrow on a dwarf"
  target can be expressed without further obs changes.
  - [x] 7a. Arrow North
  - [x] 7b. Arrow East
  - [x] 7c. Arrow South
  - [x] 7d. Arrow West

Action space is now MultiDiscrete([14, 4, 30, 30]) = (action, camera, x col, y
row) — the 4-camera redesign added the camera dim and shrank x/y to the 0-29
window of the chosen digger-following camera. Full list and the per action rules
live in docs/PROTOCOL.md.

---

# Training-readiness TODO

The multi-world infra (Path C) is built and passing its correctness gate (see
docs/MULTIWORLD.md), so the open items are now about *learning*, not *running*.
Roughly in the order they'd bite a real run.

**Note:** the three *learning* items below (PPO tuning, action masking, reward
shaping) are now **designed within [docs/PIVOT.md](dwarfs-rl/docs/PIVOT.md)** — the
per-dwarf pivot changes the obs/action/reward they refer to, so do them in that
context rather than against the camera setup. The combat / parked-world /
throughput items are independent multi-world hardening and stand on their own.

- [ ] **Validate combat under multi-world.** Every isolation/determinism/parity
  proof was run in a **no-combat regime** (monster caves are rare; test episodes
  too short to dig into one). The only shared mutable game state across worlds is
  the two static enemy-ID counters (`m_iBossIDPool`/`m_iMinionIDPool`), and
  they're touched exactly when enemies spawn — i.e. the one untested path is also
  the one place cross-world contamination could occur. Force a combat scenario
  and re-run the same-seed isolation check with enemies active. (docs/MULTIWORLD.md §6)
- [ ] **Rebuild-on-demand for a parked world.** A STEP that throws parks that
  world for the rest of the run; there's no rebuild. Over a long PPO run, parked
  worlds silently shrink effective parallelism (SubprocVecEnv expects every env
  to keep resetting forever). Confirm what a parked world returns to its worker on
  the next `reset()`. (docs/MULTIWORLD.md §6)
- [ ] **Tune PPO.** *(→ PIVOT.md §5–§7.)* `train.py` uses SB3 defaults bar
  `learning_rate=3e-4`. Long episodes (~15k frames), sparse score-delta reward, and
  ~20k-step rollouts are a poor fit for default `n_steps`/`batch_size`/`n_epochs`/
  `gamma`/entropy. CPU-only (AMD, no CUDA). The pivot drops the CNN for a small MLP
  and sets `γ` from the arrow-traversal length.
- [ ] **Action masking.** *(→ PIVOT.md §4/§7.)* Most actions are refused (no eligible
  dwarf, illegal tile, not enough gold). The pivot makes this first-class: per-dwarf
  masking of illegal choices via `MaskablePPO` (sb3-contrib), with a dominant no-op
  so the valid-action rate stays high. Measure the valid-action fraction early
  regardless; if it's tiny, learning crawls.
- [ ] **Reward shaping.** *(→ PIVOT.md §5.)* Score is flat for long stretches then a
  big terminal penalty — classic hard-RL shape. The pivot adds per-dwarf event
  attribution (e.g. lava-seal credited to the breaching dwarf) and potential-based
  distance shaping. Weights still arrive on RESET (Python-side, no mod rebuild).
- [ ] **Large-map / core-count throughput.** Multi-world throughput was profiled
  only to 10 worlds on small Easy maps, never to the core-count ceiling nor on the
  harder difficulties' 1000x1000 maps (much heavier `UpdateGamefield`/frame).
  (docs/MULTIWORLD.md §6)
