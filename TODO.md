# Action Space TODO

Actions to expose to the model so it can learn to play. Each unchecked box is an
action still to be wired through the mod's `ApplyAction` and the env's action space.

- [ ] **0. Do nothing**
- [ ] **1. Speed up time** *(most important)*
- [ ] **2. Place dynamite**
- [ ] **3. Place tower**
- [ ] **4. Place wall**
- [ ] **5. Reinforce wall**
- [ ] **6. Tower actions**
  - [ ] 6a. Toggle digger dwarf spawner
  - [ ] 6b. Spawn warrior dwarf
  - [ ] 6c. Recall all warrior dwarfs
  - [ ] 6d. Cannon dwarfs
  - [ ] 6e. Toggle train (xp) existing warrior dwarfs
- [ ] **7. Arrow actions** — needs a way to target on top of a dwarf, plus a
  system to let it target a POI of some kind (e.g. cave, minerals, etc.)
  - [ ] 7a. Arrow North
  - [ ] 7b. Arrow East
  - [ ] 7c. Arrow South
  - [ ] 7d. Arrow West
