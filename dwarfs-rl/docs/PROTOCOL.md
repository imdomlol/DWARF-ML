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
{ "command": "STEP", "action": 2, "camera": 0, "x": 15, "y": 10 }
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

STEP takes `camera` (0–3) to pick which of the 4 digger cameras to aim at, then
`x` / `y` in that camera's window coordinates (column and row, 0–29). the mod
translates to map tiles using where that camera's crop sits. if the camera slot
has no digger assigned (zero-filled camera) the action is refused. placement runs
through the same validation the games own click handlers use, wrong spot or
occupied square or not enough gold and the action gets refused (check `action_ok`).

`RENDER` is for spectating. `enabled` toggles drawing and `max_fps` paces the
frames (60 = real time, 120 = double speed, 0 or missing = unlimited).
training runs with rendering off and no pacing. heads up, the pacing is
independent of drawing, so a `max_fps` (or the RESET `render_fps`) bigger than 0
throttles the sim even with rendering off. thats handy as a cpu governor (cap a
headless run so the machine stays usable) but it also means you dont want a
stray frame cap on a run you meant to be full speed.

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
* `instant_seal` bonus added per instant-seal achievement the game awards this
  step (`iInstantSeal` in PointlessData). the game awards this when a cave is
  sealed very quickly after it opens. default 0. set a positive value to reward
  fast cave responses
* `render` / `render_fps`. episodes start headless by default, pass render
  true (plus a render_fps like 60 if you want it watchable) to see the run.
  every RESET reapplies these so set them per episode, or use the RENDER
  command / the control panel to flip drawing mid run

## replies, mod to env

every RESET and STEP gets exactly one reply and it always carries all four of
the required fields (the env indexes straight into them)

```jsonc
{
  // 4 cameras, each a triple of flat arrays (terrain / dwarves / enemies), 30x30 each.
  // camera 0 = northmost digger, 1 = eastmost, 2 = southmost, 3 = westmost.
  // zero-filled when that camera slot has no digger.
  "cam0_terrain": [0, 1, 2, ...],   // flattened 1d, row major (30 rows x 30 cols)
  "cam0_dwarves": [0, 1, 0, ...],   // dwarves in this window (0 none, 1 digger, 2 warrior)
  "cam0_enemies": [0, 0, 1, ...],   // visible enemies (0 none, 1 minion, 2 boss)
  "cam1_terrain": [...], "cam1_dwarves": [...], "cam1_enemies": [...],
  "cam2_terrain": [...], "cam2_dwarves": [...], "cam2_enemies": [...],
  "cam3_terrain": [...], "cam3_dwarves": [...], "cam3_enemies": [...],
  "cam_origins": [nx,ny, ex,ey, sx,sy, wx,wy], // camera crop origins on the map (-1 if no dwarf)
  "immediate_reward": 5.0,      // score gained since the previous reply
  "terminated": false,          // episode hit a terminal state (time up / city dead)
  "truncated": false,           // reserved, the time limit counts as terminated
  "gold": 250,                  // current gold
  "score": 70,
  "dwarves": 3,                 // live dwarf count
  "time_left": 18899,           // episode countdown in frames
  "city_hp": 510,               // base health
  "cost_wall": 30,              // gold cost to place a wall (fixed per difficulty)
  "cost_dynamite": 50,          // gold cost to place dynamite (fixed per difficulty)
  "cost_arrow": 10,             // gold cost to place a green arrow (fixed per difficulty)
  "cost_tower": 200,            // gold cost to place the NEXT tower (escalates each build)
  "cost_warrior": 40,           // gold cost to spawn a warrior (fixed per difficulty)
  "cave_opened": 0,             // 0 = no event; nonzero = m_iType of the newly opened cave
  "cave_x": -1, "cave_y": -1,  // map coords of the cave origin (-1 if no event)
  "instant_seal_delta": 0,      // iInstantSeal achievement counter delta this step
  "action_ok": true,            // did the last action actually apply
  "tick": 123                   // frames elapsed, handy for debugging
}
```

tile codes in the terrain arrays. `0` undug soil, `1` open ground / dug out
tunnel, `2` placed stone wall, `3` rock (solid, undiggable), `4` mineral
deposit, `5` water, `6` lava.

no xray. sealed caves are masked to plain soil til the dwarves actually break
into them so the model sees exactly what a human sees, water and lava only
become visible once discovered.

the dwarves layer marks `0` empty, `1` a regular (digger) dwarf, `2` a warrior.
no fog masking, you always see your own dwarves like a human does.

the enemies layer marks `0` none, `1` a minion, `2` a boss. only enemies a human
could see are marked; tunneling or fully stealthed ones are left out.

**cave alert**: when a dwarf digs into a water or lava cave during a step,
`cave_opened` is set to the cave's internal type (nonzero) and `cave_x/y` give
its map position. the terrain layer will already show water or lava appearing at
that tile. the `instant_seal_delta` field increments whenever the game awards its
own "instant seal" achievement (very fast response after cave opening) — use the
`instant_seal` RESET weight to reward that behaviour.

## map size and the camera system

the real playfield is big, 500x500 on easy up to 1000x1000 on the hardest
setting (it scales with difficulty not with the time category). instead of a
fixed window around the city, the mod follows the action: 4 cameras (30x30 each)
track the northmost/eastmost/southmost/westmost digger dwarf. this puts the model
wherever the frontier is without losing coverage when dwarves spread out. when
fewer than 4 diggers exist the missing slots are zero-filled.

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
