# whats new

quick rundown of what i changed so youre not going in blind. the wire format
stuff is still in [PROTOCOL.md](PROTOCOL.md).

> **superseded (2026-06-28):** the entries below predate the 4-camera obs
> redesign. the action space is now `MultiDiscrete([14, 4, 30, 30])` =
> `(action, camera, x, y)` (a camera dim was added; x/y are 0-29 within the
> chosen camera's 30x30 window), **not** the `[14, 60, 40]` written below. the
> obs also gained a per-camera **dwarves layer** (1 digger, 2 warrior), so the
> "no dwarf positions in the obs" caveat under *new actions* no longer holds.
> [PROTOCOL.md](PROTOCOL.md) and `python/dwarfs_env.py` are the source of truth.
>
> **what's next:** the whole camera observation + `[action, camera, x, y]` action is
> slated to be replaced by a per-dwarf entity model (observe/command all dwarves at
> once, arrows instead of tile-picking). see [PIVOT.md](PIVOT.md) — planned, not yet
> built.

new actions are in the mod so just pulling wont get them, you gotta rebuild and
re patch

```
git pull
dotnet build mod -c Release
dotnet build loader
loader\bin\Debug\net8.0\dwarfsloader.exe
```

python side comes with the pull.

## new actions

action space is `[14, 60, 40]` now instead of `[7, 60, 40]`. new ones on top of
what you had

* `7` place tower
* `8` reinforce wall
* `9` toggle digger spawner on a tower
* `10` spawn warrior at a tower
* `11` recall a towers warriors
* `12` cannon strike
* `13` toggle warrior training

for 9 to 13 the x/y tile just points at the tower, anywhere in its 3x3 works.
`12` is the odd one, the tile is the target you fire at, not the tower.
everything else takes a tile like walls do. refused ones still come back with
action_ok false.

two notes

* reinforce `8`, the game doesnt actually have a reinforce so i made it pay the
  wall cost to fix a damaged wall back to full. if you meant Solidify instead
  lemme know
* the arrows work but the targeting thing you wanted (arrow on a dwarf, aim at a
  poi) isnt in. minerals are already in the obs so it can aim at those, but
  putting an arrow on a dwarf means adding dwarf positions to what the model
  sees, didnt wanna change that without asking you first

## --power flag

put a `--power` on train.py so you can dial how hard it runs

```
python python/train.py --power moderate
```

* `max` runs a bunch of instances flat out, whole pc
* `moderate` about half your cores, this is the one id use
* `min` one instance throttled way down, for leaving it going in the background
  while you use the pc

`--instances` still overrides the count if you wanna set it yourself.

## watch out, the speed cap

old `--render-fps` defaulted to 60 and got sent even headless, and that pacing
runs headless too, so if you ever ran train.py without `--render-fps 0` it was
stuck at 60fps the whole time. fixed, headless runs full tilt now unless you tell
--power to throttle.

## on instances

messed with the counts, 4 is about as fast as 7, past that the extra games just
end up waiting on the trainer so they dont really add anything. id just run 4.
and dont bother with the gpu, its an amd card so theres no cuda and the net is
tiny anyway, cpu is fine.
