# setup

everything here runs on the machine that has the game. the loader patches a
*copy* of dwarfs, your steam install never gets touched.

## what you need first

1. **steam + Dwarfs!? F2P installed.** launch the game once normally through
   steam and quit at the menu. that first boot installs the xna 3.1 runtime and
   might prompt windows to enable .NET 3.5, say yes. the patched copy needs both
2. **.NET sdk 8** to build the mod and the loader
   ```
   winget install Microsoft.DotNet.SDK.8
   ```
3. **python 3.10+** with the websockets package for the env side
   ```
   pip install websockets
   ```

## build and patch

from the repo root

```
dotnet build mod -c Release
dotnet build loader
loader\bin\Debug\net8.0\dwarfsloader.exe
```

if your steam library isnt in the default spot point the loader at it

```
dwarfsloader.exe --game "D:\SteamLibrary\steamapps\common\Dwarfs - F2P"
```

the loader prints a `verify:` line and every hook should say True. the runnable
patched copy lands in `game-patched\` (game assets are junctioned not copied so
its small).

## run

1. start your env / training script first, it owns the websocket server on
   `localhost:8765`. `python\dwarfs_env.py` is a ready gymnasium env to build
   on (`pip install gymnasium numpy` for it) and `python python\random_agent.py`
   is a quick demo of it. for a plumbing check without any of that use
   `python python\fake_env.py`
2. with steam running start `game-patched\Dwarfs.exe`
3. the mod connects within a few seconds and the env takes over from there.
   RESET starts a round, STEP drives it, see PROTOCOL.md for the full wire
   format

a control panel window opens next to the game with live stats, a render toggle
and a place to launch the training script from. set `DWARFS_BRIDGE_GUI=0` in
the environment if you dont want it.

## several instances at once

each instance reads `DWARFS_BRIDGE_PORT` at boot. start one websocket server
per port on the env side then launch the games a few seconds apart

```powershell
$env:DWARFS_BRIDGE_PORT = "8766"
Start-Process game-patched\Dwarfs.exe
```

`python\parallel_test.py N` drives N instances and reports combined
throughput. more notes in PROTOCOL.md.

## when somethings off

* the mod logs to `%TEMP%\dwarfs_mod.log` (or `dwarfs_mod_<port>.log` on a non
  default port). boot, connection, resets and any refused actions all show up
  there
* "send skipped, not connected" means the envs server wasnt up yet. the mod
  retries every few seconds so just start the env and wait
* a black or frozen looking game window during training is normal, rendering
  is suppressed while an episode runs headless. it comes back when the env
  turns rendering on or training stops
* if the game sits at the menu doing nothing the env hasnt sent RESET yet
* headless training still counts as steam playtime and can pop achievements.
  yes really
* your antivirus might flag or quarantine the loader, the mod dll or the patched
  game. its unsigned code that rewrites a game exe and runs inside it, which is
  exactly the shape of the stuff a scanner watches for, so a strict one like
  bitdefender can grab it. its a false positive. whitelist the dwarfs-rl folder
  and the game-patched folder and youre good. signing the binaries would fix it
  properly but that needs a paid certificate
* if the start training button in the panel itself trips the antivirus, just run
  your training script from a terminal instead. the button was only launching it
  for you, nothing more
* feels slow, like its capped around real time? thats action_repeat sitting at 1,
  every single frame is waiting on a full decision from your model. bump it (8 is
  a good starting point) so one decision covers 8 frames and you get way more
  game frames per inference. the gym env takes it as a constructor arg, or add
  an "action_repeat" field to your RESET message
