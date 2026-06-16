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
- [~] **7. Arrow actions** — the four directional arrows are wired (actions 3-6)
  and minerals already show up in the observation (tile code 4) so the model can
  aim at those today. The open part is the targeting help you wanted: putting an
  arrow "on top of a dwarf" needs dwarf positions in the observation, which it
  doesn't have yet (no dwarf layer). Adding one reshapes the obs your model
  trains on so I left it for you to ok first.
  - [x] 7a. Arrow North
  - [x] 7b. Arrow East
  - [x] 7c. Arrow South
  - [x] 7d. Arrow West

Action space is now MultiDiscrete([14, 60, 40]) = (action, x col, y row). Full
list and the per action rules live in docs/PROTOCOL.md.
