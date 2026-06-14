# dwarfs RL environment

training setup for a reinforcement learning agent that plays Dwarfs!? (the steam
F2P build). the game has no api so instead of scraping pixels off the screen we
hook it from the inside. a mod loaded into the game process exposes the live
game state and takes actions over a local websocket and the python side wraps
that as a gym environment.

the game is xna / c# which means its actual classes (Game1, GameMap, Resources,
Dwarf etc) are readable and reachable. so we get clean observations and an
exact reward and dont need any vision model in the loop.

## what works

* the env drives the game in lockstep. RESET starts a fresh arcade round (mode,
  difficulty, seed and reward weights all picked per episode), STEP advances it
  one decision at a time, and every reply carries the map window, gold/score/
  dwarf counts and the episode flags
* no speed limit. the game only reads the clock for an fps counter so one
  Update call is one deterministic sim tick no matter how fast they come.
  measured 40x+ real speed on a single instance and you can run multiple game
  instances in parallel for more
* same rules as a human. actions go through the games own placement checks and
  sealed caves are masked out of the observation til the dwarves actually dig
  into them
* reproducible, same seed + same actions = the same episode byte for byte

## layout

* `mod/` is the in process mod (.NET 3.5, matches the games runtime). hooks the
  update loop, packs game state into json, applies actions, owns the clock and
  the reward
* `loader/` patches a copy of Dwarfs.exe so it boots the mod on startup. never
  touches the steam install
* `python/` is the env + test harnesses. `dwarfs_env.py` is a ready gymnasium
  environment to train against and `random_agent.py` runs it with random
  actions as a demo / baseline. `fake_env.py` (full regression of the
  protocol), `full_run.py` (times a complete episode) and `parallel_test.py`
  (several instances at once) are for testing the mod itself

## getting started

see [SETUP.md](SETUP.md) for install/build/run and [PROTOCOL.md](PROTOCOL.md)
for the wire format the env speaks.
