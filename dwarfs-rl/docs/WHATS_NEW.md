# whats new

latest batch of changes, the new actions and a power flag for the trainer plus
a speed fix you'll want to know about. the wire format details still live in
[PROTOCOL.md](PROTOCOL.md), this is just the new stuff and how to drive it.

heads up before anything else: the new actions live in the mod, so a `git pull`
on its own wont give them to you. you have to rebuild and re patch

```
git pull
dotnet build mod -c Release
dotnet build loader
loader\bin\Debug\net8.0\dwarfsloader.exe
```

the python side (the env + train.py) just comes with the pull, no rebuild there.

## the new actions

the action space grew from `MultiDiscrete([7, 60, 40])` to
`MultiDiscrete([14, 60, 40])`, still `(action, x col, y row)`. the full set now

* `0` idle
* `1` place dynamite
* `2` place wall
* `3` to `6` green arrow up / right / down / left
* `7` place tower (an outpost)
* `8` reinforce wall
* `9` toggle the digger spawner on a tower
* `10` spawn a warrior at a tower
* `11` recall all of a towers warriors
* `12` cannon strike
* `13` toggle warrior training on a tower

the x / y tile means the same window coords as before for `1` `2` `7` `8` and
the arrows, the spot youre acting on. for the tower actions `9` to `13` the tile
picks which tower, anywhere inside its 3x3 footprint counts. the one odd one out
is `12` the cannon, there the tile is the target you want to fire at, the mod
finds a tower in range with warriors home and lobs them at it.

all of them run through the games own validation just like walls did, so a bad
spot or not enough gold gets refused and `action_ok` comes back false in the
reply. nothing changed about how you read that.

two things worth knowing about specific ones

* `8` reinforce wall. the base game doesnt actually have a reinforce, walls just
  have health, so i defined it as paying the wall cost to patch a damaged wall
  back to full. no wall on that tile or its already full and it gets refused. if
  you wanted it to mean Solidify (turn soil into permanent rock) instead, say so,
  thats a real handler too and an easy swap
* the arrows (`3` to `6`) work but the smarter targeting you asked for (drop an
  arrow right on a dwarf, aim at a point of interest) isnt in yet. minerals
  already show up in the obs as tile code 4 so the model can aim at those today,
  but targeting a dwarf needs dwarf positions added to the observation, which
  changes the shape your model trains on, so i left that for you to ok first

## the power flag

new `--power` on train.py, one dial for how much of the machine training eats.
it sets the instance count, a per game frame cap and how many threads pytorch
gets for inference, all at once

```
python python/train.py --power max
python python/train.py --power moderate
python python/train.py --power min
```

what each does

* `max` spins up about as many games as fit (roughly cores minus one) at full
  speed and gives pytorch a few threads. use the whole pc
* `moderate` runs about half your cores at full speed. this is the sensible
  default, honestly the one id actually train on (see the speed bit below for
  why)
* `min` is one instance, throttled down to a low frame rate, one pytorch thread.
  for when you wanna leave it training in the background and still use the pc

`--instances` still wins if you pass it, so you can mix them. power picks the
rest

```
python python/train.py --power max --instances 3
```

with no `--power` at all train.py behaves exactly like before.

## heads up, headless speed got fixed

old behaviour had a footgun. `--render-fps` defaulted to 60 and got sent to the
game even on a headless run, and the frame pacing works whether or not the game
is drawing, so any training run that didnt pass `--render-fps 0` was quietly
capped at 60 fps the whole time. if you ever ran train.py straight, this was
throttling you hard.

fixed now. headless runs go full speed unless `--power` asks for a throttle. the
frame cap still exists and still works, its just yours to turn on on purpose now,
which doubles as a handy cpu governor if you want to throttle a background run.

## what kind of speed to expect

careful with the units here, two different per second numbers get mixed up

* frames per second is how fast the game world ticks
* steps per second is training samples per second, the actual data rate, and
  this is what stable_baselines3 reports as `fps`. it already adds up every
  instance

measured on an 8 core box, all at action_repeat 8 with the real trainer running

* 1 instance, about 120 samples/s, about 960 frames/s
* 4 instances, about 234 samples/s, about 1870 frames/s
* 7 instances, about 243 samples/s, about 1940 frames/s

the big takeaway: 4 instances and 7 instances pull almost the same data rate.
going past ~4 barely helps cause the bottleneck isnt the games, its the trainer
loop (inference plus the syncing where every game waits its turn each step). a
single game on its own does ~340 steps/s with no trainer, but inside ppo each one
only manages ~58, so the games sit idle most of the time waiting. so run ~4, not
7, same speed and half the footprint.

also a heads up on the gpu since it came up: your card is amd, and pytorch cuda
is nvidia only, theres no clean gpu path on windows for it. for a tiny policy
net at this batch size it wouldnt help anyway, the cpu is fine. the real way past
~240 samples/s is the trainer side, an async collection loop so the games stop
idling, thats your call on the ppo side not the env.

## a few calls i need from you

nothing more to build on my end, just three decisions, all written up in
[TODO.md](../../TODO.md) too

1. speed up time. i dug into it and it isnt really an agent action, the round
   timer is frame locked so it doesnt speed training up, and forcing the in game
   speed makes the sim run fast against the clock which warps the game. action_repeat
   is the honest version of go faster and we already have it. ok to drop it?
2. the arrow targeting from above, do you want the dwarf positions added to the
   obs so the model can aim at dwarves. its a change to what your model sees so
   its your call
3. reinforce wall, is repair what you meant or did you want Solidify

## nothing new to install

no new libraries, the requirements file is unchanged and i didnt add any nuget
packages. train.py imports torch now but thats only to set its thread count and
torch was already a dependency through stable_baselines3. so a pull and a rebuild
is all you need.
