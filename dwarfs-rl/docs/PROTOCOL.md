# bridge protocol

the gym environment runs a **websocket server** on `ws://localhost:8765` and
the mod **connects to it** when the game starts (retrying every few seconds til
the env is up, reconnecting if it drops). messages are json text frames, one
per command / reply. the env busy waits for exactly one reply per command and
the mod sends that reply from the game thread so the state in it is never mid
frame.

## commands, env to mod

```jsonc
{ "command": "RESET", "mode": "m5", "difficulty": "Easy", "seed": 1234, "action_repeat": 4 }
{ "command": "STEP", "action": 2, "x": 31, "y": 18 }
{ "command": "RENDER", "enabled": true, "max_fps": 60 }
{ "command": "QUIT" }
```

actions, the full set

* `0` idle, coordinates ignored
* `1` place dynamite
* `2` place wall
* `3` to `6` place a green arrow pointing up / right / down / left. arrows are
  what steers the dwarves so theyre the main scoring tool, walls and dynamite
  are the defense. they cost 5 on easy, can sit on undug soil too and fade out
  after about a minute of game time
* `7` place tower (an outpost). needs a clear discovered 7x7 of open ground and
  enough gold, cant sit on the city. towers spawn and train warriors and diggers
* `8` reinforce wall. the base game has no reinforce so this is defined as paying
  the wall cost to patch a damaged wall at the tile back to full health. no wall
  there or already full and it gets refused
* `9` to `13` are tower actions, the tile picks which tower (anywhere in its 3x3
  footprint). `9` toggle the digger spawner, `10` spawn a warrior (gold + max
  warrior checks), `11` recall all that towers warriors, `12` cannon strike where
  for this one the tile is the **target** to fire at (the mod picks a tower in
  range with warriors home and lobs them at the spot), `13` toggle warrior
  training

anything that targets a tile takes `x` / `y` in **window coordinates** (the same
grid the observation shows, x is the column and y the row), the mod translates to
map tiles using where the crop sits. placement runs through the same validation
the games own click handlers use, wrong spot or occupied square or not enough
gold and the action gets refused (check `action_ok` in the reply).

`RENDER` is for spectating. `enabled` toggles drawing and `max_fps` paces the
frames (60 = real time, 120 = double speed, 0 or missing = unlimited).
training runs with rendering off and no pacing.

`QUIT` shuts the game down cleanly through its own exit path so the trainer
doesnt have to kill the process.

RESET fields, all optional

* `mode` is `m5` / `m15` / `m30` / `m60`, which arcade time category to start,
  so a separate model can be trained per category. default m5
* `difficulty` is `Easy` / `Normal` / `Hard` / `TediHardcore`. picks the
  leaderboard bracket and with it the map size (500/700/850/1000 square) plus
  the costs and flow speeds. default Easy
* `seed` is the level generation seed. same seed same map, fix it for eval and
  randomize for training. leave it out for the games own randomness
* `action_repeat` is how many frames each STEP advances (1 to 240, default 1).
  one decision spanning N frames divides episode length by N
* `death_penalty` gets subtracted from the reward when the episode ends before
  the timer (city flooded / destroyed). default 1500. natural timeout costs
  nothing
* `invalid_action` gets subtracted whenever an action is refused (`action_ok`
  false). default 0, a small value like 0.05 teaches action legality faster
* `hazard_penalty` gets subtracted for every new water or lava tile that spreads
  in a step, so the agent gets punished for letting a flood run instead of
  plugging it fast. default 0 (off). a sealed cave sitting still costs nothing,
  only the active spread counts, and the map only gets scanned when this is non
  zero so it stays free when off
* `render` / `render_fps`. episodes start headless by default, pass render
  true (plus a render_fps like 60 if you want it watchable) to see the run.
  every RESET reapplies these so set them per episode, or use the RENDER
  command / the control panel to flip drawing mid run

## replies, mod to env

every RESET and STEP gets exactly one reply and it always carries all four of
the required fields (the env indexes straight into them)

```jsonc
{
  "map_grid": [0, 1, 2, ...],   // flattened 1d int grid, row major (40 rows x 60 cols)
  "immediate_reward": 5.0,      // score gained since the previous reply
  "terminated": false,          // episode hit a terminal state (time up / city dead)
  "truncated": false,           // reserved, the time limit counts as terminated
  "gold": 250,                  // current gold (the model needs this to know
  "score": 70,                  //   what it can afford)
  "dwarves": 3,                 // live dwarf count
  "time_left": 18899,           // episode countdown in frames
  "city_hp": 510,               // base health
  "action_ok": true,            // did the last action actually apply
  "crop_x": 220, "crop_y": 230, // where the window sits on the full map
  "tick": 123                   // frames elapsed, handy for debugging
}
```

tile codes in `map_grid`. `0` undug soil, `1` open ground / dug out tunnel,
`2` placed stone wall, `3` rock (solid, undiggable), `4` mineral deposit,
`5` water, `6` lava.

no xray. sealed caves are masked to plain soil til the dwarves actually break
into them so the model sees exactly what a human sees, water and lava only
become visible once discovered. mineral veins sitting in soil stay visible
cause the game draws those in the walls for human players too.

## map size and the crop window

the real playfield is big, 500x500 on easy up to 1000x1000 on the hardest
setting (it scales with difficulty not with the time category). shipping the
whole thing every step would be megabytes of json per frame and no model wants
a raw 700x700 input anyway.

so the mod sends a **fixed size window cropped around the city** (the base
never moves), currently 60 wide x 40 tall to match the envs observation space.
the window size is a mod side constant for now, if we want the agent to see
further we bump it on both sides or add downsampled outer rings later.

## running several instances in parallel

the intended training shape is N copies of the patched game each connected to
its own env worker all stepping at the same time (standard vectorized env
setup, SubprocVecEnv and friends). verified working, steam included.

* each worker runs its own websocket server on its own port (8765, 8766, ...)
* point each game at its worker by setting `DWARFS_BRIDGE_PORT` (and
  `DWARFS_BRIDGE_HOST` if needed) in the environment before launching that copy
* stagger the launches by a couple seconds, every instance writes
  `steam_appid.txt` into the game folder at boot and simultaneous first writes
  can collide
* non default ports log to `%TEMP%\dwarfs_mod_<port>.log` so the logs dont
  interleave
* sizing wise each instance is a full game process, one per physical core
  minus a couple for the trainer is a sane starting point

## timing

there is no speed cap. the game is frozen between commands and computes each
tick flat out when one arrives, so wall clock speed is decided entirely by the
trainer. python round trip latency + model inference + the games per tick cpu
cost. the sim is purely frame based (it never reads the wall clock) so one
Update is always exactly one tick and the physics is identical at any speed.

levers, fastest first

* `action_repeat` on RESET. one decision spans N frames with no round trip in
  between, the biggest multiplier. repeat 8 turns a 5 minute episode into a
  few seconds of wall time
* a faster python loop / batched inference. the round trip (1.5 to 2 ms
  measured) dominates once the game side is unthrottled
* `max_fps` on RENDER exists for the opposite case, slowing down to watch

measured on a mid desktop, 480 to 525 steps/s with full observation traffic,
1200 steps/s for idle steps, about 40 ms per episode reset.

## reward / episode logic

lives in the mod not the env (the env stays a thin translator) but the weights
come in on RESET so tuning happens python side with no rebuilds.

current shape, `immediate_reward = score delta` each step (the games own
arcade objective which already counts territory dug, gold, kills and dwarf
deaths), minus `death_penalty` on a lost round, minus `invalid_action` per
refused action. losing early is what the penalty is for. the fatal mistake
(breaching a water cave) happens well before the flood hits the city and the
terminal penalty is what marks those runs as bad. dying isnt over punished
though, dwarves dig on their own so a policy cant just freeze to stay safe, it
has to learn walls.

`terminated` = timer hit zero, city destroyed, or the game left the playing
state. `truncated` stays false, the time limit is the natural end of an arcade
round not a training cutoff.
