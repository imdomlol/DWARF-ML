# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An RL training setup for the game **Dwarfs!?** (Steam F2P, XNA/C#). The game has no API, so instead of pixel-scraping it is hooked from the inside: a mod loaded into the game process exposes live game state and applies actions over a local WebSocket, and a Python Gymnasium environment wraps that as a standard RL env. Because the mod reads actual game classes via reflection, observations and reward are exact and no vision model is needed.

Three subsystems talk over one WebSocket (`ws://localhost:8765`, JSON text frames, strict lockstep — one reply per command):
- **`dwarfs-rl/mod/`** (.NET 3.5, matches the game runtime) — the in-process mod. Hooks the update/input/draw/level-gen methods, packs game state into JSON, applies actions, owns the clock and the reward. Never references the game or XNA directly; everything is reflection at runtime so the build needs no game DLLs.
- **`dwarfs-rl/loader/`** (.NET 8) — patches a *copy* of `Dwarfs.exe` (via Mono.Cecil) to boot the mod and weave calls into it. Never touches the Steam install; the patched copy lands in `game-patched/` with `Content/` junctioned back to the original.
- **`dwarfs-rl/python/`** — the Gymnasium env (`dwarfs_env.py`) plus test harnesses.

The wire format is the contract between all three. **`dwarfs-rl/docs/PROTOCOL.md` is the source of truth** for commands, reply fields, action codes, tile codes, and the camera system. Read it before changing anything that crosses the socket — a change to the message schema must land in `mod/World.cs` (`BuildState`/`HandleCommand`) and `python/dwarfs_env.py` together.

## Repo layout caveat (important)

`dwarfs-rl/` is the canonical project root; the repo root holds only a thin index README, the `.gitignore`, and `TODO.md`. Everything that runs lives under `dwarfs-rl/` — the mod, the loader, and `python/` (the env `dwarfs_env.py`, the training entrypoint `train.py`, and the test harnesses). Reference docs live in `dwarfs-rl/docs/` (`SETUP.md`, `PROTOCOL.md`); `docs/PROTOCOL.md` is the source of truth for the wire format. (Historical note: an earlier `dwarf_mod_env.py` env and a `mod_specs.md` doc were removed during consolidation — `TODO.md` tracks the restructure.)

## Commands

Build the mod and loader, then patch the game (run from the `dwarfs-rl/` directory — paths in docs/SETUP.md are relative to it):
```
dotnet build mod -c Release
dotnet build loader
loader\bin\Debug\net8.0\dwarfsloader.exe          # add --game "<steam path>" if not default
```
The loader prints a `verify:` line; every hook should say True. Requires .NET SDK 8 and a once-launched Steam install of Dwarfs!? F2P (first boot installs the XNA 3.1 runtime + .NET 3.5).

Python setup and run (also from `dwarfs-rl/`):
```
pip install -r requirements.txt        # torch, gymnasium, stable_baselines3, websockets, numpy, tensorboard
python python/train.py                       # PPO training (MultiInputPolicy)
python python/train.py --instances 4         # train across 4 game instances at once
python python/train.py --instances 10 --multiworld   # 10 worlds in ONE process, one GPU device (Path C)
python python/train.py --instances 4 --multiworld --tensorboard   # + log curves to ./runs
python python/train.py --mode demo --render  # random-action demo with rendering
```
`train.py` flags: `--mode train|demo`, `--timesteps N`, `--steps N`, `--render`, `--render-fps N`, `--instances N` (spin up N games and train across them via SubprocVecEnv), `--multiworld` (run all instances as worlds inside ONE game process sharing one GPU device — Path C, see docs/MULTIWORLD.md — so the instance count scales on CPU/RAM past the GPU's ~2–3 device ceiling), `--tensorboard` / `--logdir DIR` / `--run-name NAME` (write TensorBoard logs of episode reward/length, losses, fps and game score to `./runs`; view with `tensorboard --logdir ./runs`. Logs go to a stable `--logdir/--run-name` folder, `ppo` by default, reused across `--resume` so a resumed run is one continuous curve instead of a new `ppo_N` line), `--resume` (continue from `dwarfs_agent.zip` if it exists — restores the timestep counter and keeps the TB x-axis going; default with no flag always starts a fresh random model so a stale save is never silently continued), `--checkpoint-freq N` (save a checkpoint to `./checkpoints` every N total steps, 0 = off — crash/Ctrl+C safety; the run also saves once on exit via a try/finally, even on KeyboardInterrupt), `--power max|moderate|min` (one dial for how much of the machine to use — sets the instance count unless `--instances` is given, a per-game frame cap, and PyTorch's thread count; `max` ≈ cores−1 at full speed, `min` = one throttled instance). Episodes are always wrapped in a Monitor so reward/length show up in the logs; a `GameMetricsCallback` adds the game score. It imports the env as `from dwarfs_env import DwarfsEnv` (same `python/` dir), so run it from `dwarfs-rl/` as shown. Note: the frame cap (`render_fps`) paces the game even headless, so it doubles as a CPU governor — `train.py` only applies it to headless runs when `--power` asks, otherwise headless runs full speed.

**Run order matters: start the Python side first** (it owns the WebSocket *server*; the mod is the *client* and connects to it, retrying every few seconds). Then launch `game-patched\Dwarfs.exe`.

Test harnesses in `dwarfs-rl/python/` (these exercise the live mod, not the trainer):
```
python python/fake_env.py        # full protocol regression, no gym/torch needed — plumbing check
python python/full_run.py        # times a complete episode
python python/parallel_test.py N # drives N game instances at once, reports combined throughput
python python/random_agent.py    # runs the gym env with random actions (baseline/demo)
```

## Key mechanics to know before changing things

- **Lockstep & timing.** The game's sim is purely frame-based — `Game1.Update` never reads the wall clock, so one Update = one deterministic tick regardless of speed. While an episode is live, `Bridge.BeforeUpdate` gates each frame on a command from the env (single-slot mailbox). There is no speed cap; wall-clock speed is set entirely by the Python loop. `action_repeat` (RESET field, 1–240) advances N frames per STEP and is the biggest throughput lever.
- **Observation is 4 digger-following cameras.** Instead of a fixed window around the city, the mod tracks the northmost/eastmost/southmost/westmost **digger** dwarf and crops a **30×30** window around each. `CamW`/`CamH` are constants in `mod/World.cs`; `CAM_W`/`CAM_H` in `python/dwarfs_env.py` must match. Each camera has 3 channels (terrain, dwarves, enemies) stacked into shape `(3, 30, 30)`. Obs dict keys: `cam0` (north digger), `cam1` (east), `cam2` (south), `cam3` (west), plus `stats` (9 scalars: gold, dwarves, time_left, city_hp, 5 build costs). Zero-filled when a camera slot has no digger. Dwarves layer: `1` digger, `2` warrior; enemies layer: `1` minion, `2` boss (masked to human visibility). Cave events: `cave_opened` (nonzero = cave type), `cave_x/y` (map coords), `instant_seal_delta` (achievement delta) are in `info`. Enemies come from digging into monster caves (every difficulty). `GRID_W/GRID_H` constants and `map_grid/dwarf_grid/enemy_grid` JSON keys are gone.
- **Action space.** MultiDiscrete `[action, camera, x, y]` — 14 action types, camera 0–3, x/y 0–29 in that camera's window. The mod translates using the camera's crop origin; if the camera has no dwarf the action is refused. Actions: 0 idle, 1 dynamite, 2 wall, 3–6 green arrow up/right/down/left, 7 place tower, 8 reinforce wall, 9–13 tower actions (toggle digger, spawn warrior, recall, cannon, toggle train — the tile picks the tower, except 12 cannon where the tile is the target). Placement runs through the game's own validation; refused actions set `action_ok=false` in the reply (env exposes it in `info`). Full per-action rules in `docs/PROTOCOL.md`.
- **Reward lives in the mod, not the env** (the env is a thin translator). Shape: `immediate_reward = score delta` each step, minus `death_penalty` on an early loss, minus `invalid_action` per refused action. The weights arrive on RESET, so tuning happens Python-side with no rebuild.
- **Fog of war.** Sealed caves (water/lava) are masked to plain soil until dwarves dig in, so the model sees exactly what a human sees.
- **Parallel training.** Each game instance reads `DWARFS_BRIDGE_PORT` (and `DWARFS_BRIDGE_HOST`) at boot; run one env worker per port (8765, 8766, …). Set `DWARFS_BRIDGE_GUI=0` to suppress the per-instance control-panel window. Stagger launches a couple seconds apart (first-boot `steam_appid.txt` writes can collide).

## Debugging

The mod logs to `%TEMP%\dwarfs_mod.log` (or `dwarfs_mod_<port>.log` on a non-default port) — boot, connection, resets, and refused actions all show up there. `"send skipped, not connected"` means the env server wasn't up when the mod tried to connect (start the env, the mod retries). A black/frozen game window during training is normal (rendering is suppressed headless). Headless training still counts as Steam playtime and can pop achievements.
