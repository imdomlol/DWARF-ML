# DWARF-ML

Reinforcement-learning training on the game **Dwarfs!?** (the Steam F2P build).

The game has no API, so instead of pixel-scraping it is hooked from the inside: a
mod loaded into the game process exposes live game state and applies actions over
a local WebSocket, and a Python [Gymnasium](https://gymnasium.farama.org/)
environment wraps that as a standard RL env. The game is XNA / C#, so its actual
classes (`Game1`, `GameMap`, `Dwarf`, …) are readable via reflection — observations
and reward are exact and no vision model is needed.

## What works

- **Lockstep control.** RESET starts a fresh arcade round (mode, difficulty, seed
  and reward weights all picked per episode); STEP advances one decision at a time;
  every reply carries the map window, gold/score/dwarf counts and the episode flags.
- **No speed limit.** The sim only reads the clock for an fps counter, so one
  `Update` is one deterministic tick no matter how fast they come — measured 40×+
  real time on a single instance, more across parallel instances.
- **Same rules as a human.** Actions go through the game's own placement checks,
  and sealed caves are masked out of the observation until the dwarves dig in.
- **Reproducible.** Same seed + same actions = the same episode byte for byte.

## Layout

The project lives under [`dwarfs-rl/`](dwarfs-rl/):

- [dwarfs-rl/mod/](dwarfs-rl/mod/) — the in-process mod (.NET 3.5, matches the game
  runtime). Hooks the update loop, packs game state into JSON, applies actions,
  owns the clock and the reward.
- [dwarfs-rl/loader/](dwarfs-rl/loader/) — patches a copy of `Dwarfs.exe` so it
  boots the mod on startup (.NET 8). Never touches the Steam install.
- [dwarfs-rl/python/](dwarfs-rl/python/) — the env, trainer and test harnesses.
  `dwarfs_env.py` is the Gymnasium environment, `train.py` is the PPO training /
  demo entrypoint, and `random_agent.py` runs it with random actions as a baseline;
  `fake_env.py`, `full_run.py` and `parallel_test.py` exercise the mod itself.
- [dwarfs-rl/docs/](dwarfs-rl/docs/) — [SETUP.md](dwarfs-rl/docs/SETUP.md)
  (install / build / patch / run) and [PROTOCOL.md](dwarfs-rl/docs/PROTOCOL.md)
  (the WebSocket wire format — source of truth for commands, replies and the
  observation/action space).

## Getting started

See [dwarfs-rl/docs/SETUP.md](dwarfs-rl/docs/SETUP.md) for the full install / build /
run. The short version: install deps (`pip install -r dwarfs-rl/requirements.txt`),
build and patch the game, then **start the Python side first** — it owns the
WebSocket server the mod connects to — and launch the patched game.
