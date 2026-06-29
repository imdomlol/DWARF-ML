# True headless mode — options & investigation paths

Working notes for getting the game to run **without a real per-instance GPU
device**, so a swarm of training instances isn't capped by the graphics card.
This is a design scratchpad, not settled doc — `PROTOCOL.md` is still the
contract. Update this as paths get ruled in or out.

## The problem we're actually solving

Spawning N instances (`--power moderate/max`, `make_vec_env`) fails for the
extra instances with XNA's modal:

> No suitable graphics card found. Unable to create the graphics device.
> This program requires pixel shader 1.1 and vertex shader 1.1.

### What the logs say (don't re-litigate these)

Pulled from `%TEMP%\dwarfs_mod_*.log` cross-referenced with a failing run:

- The failure reproduces at a **consistent survivor count** (~2 of 4), not a
  variable one. A timing race would vary run-to-run; a stable ceiling means a
  **hard per-instance GPU resource limit**.
- It fails **with `min-gfx` ON (64×64 back buffer)**. So it is *not* the
  back-buffer / render-target VRAM that `min-gfx` shrinks. The dominant cost is
  the per-instance **content (textures/shaders)** loaded into VRAM regardless of
  back-buffer size, plus the live D3D9 device itself.
- Launches are already ~8 s apart (`--stagger 8`) and it still fails, so
  serializing *creation time* (a bigger stagger, or a cross-process mutex around
  `CreateDevice`) does **not** raise the ceiling. The live devices coexist; it's
  their simultaneous *existence*, not their creation moment, that's over budget.

**Conclusion:** the two existing knobs (`--stagger`, `--no-min-gfx` / `min-gfx`)
target a race and back-buffer VRAM respectively — neither is our bottleneck. To
go past ~2 instances we need the headless instances to stop holding a real
hardware D3D9 device / full content set.

### Cross-GPU data point (2026-06-24)

Same code, same build: an **AMD RX 5700 (8 GB)** caps at ~2–3 instances; an
**NVIDIA RTX 5070 (12 GB)** does ~6–7. The instance ratio (~2.5–3×) does **not**
track the VRAM ratio (~1.5×), and the symptom is a *caps / device-creation* modal,
not an out-of-VRAM error. That points away from "pure VRAM" and toward the **AMD
legacy D3D9 driver** being the dominant limiter on this box — exactly what a
d3d9→Vulkan layer (Path E) would bypass *if* it could be made to load. The
per-process vs per-device question (open below) is therefore still live: the
ceiling may be driver/device-count, not memory.

## Constraints any solution must respect

- **XNA 3.1 / Direct3D 9**, .NET 3.5 game runtime. No D3D11/WARP available to the
  game; `DeviceType` choices are essentially `Hardware`, `Reference`, `NullReference`.
- **The mod is reflection-only** (`mod/Bridge.cs`) — it must never reference XNA
  or the game at compile time. Everything is `GetType()/GetField/Invoke`.
- **The loader weaves call sites** (`loader/Program.cs`). New hook points = new
  woven methods + a `verify:` line entry. Existing relevant weaves:
  - `PrepareGraphics(game)` — end of the `Game1` ctor, **after** the
    `GraphicsDeviceManager` (`graphics` field) exists but **before** `Game.Run`
    creates the device. This is the one spot to subscribe to
    `PreparingDeviceSettings` / `DeviceCreated` or mutate preferred settings.
    The loader comment already anticipates a `DWARFS_BRIDGE_NULLDEVICE` switch here.
  - `Draw` is gated by `ShouldRender()` and `SuppressDraw()` — headless never
    presents a frame today, so nothing renders into the device anyway.
  - The obs is read from **game data via reflection, not pixels**, so visual
    output is already irrelevant to training. Whatever device exists only needs
    to let the sim (`Game1.Update`, `GenerateLevel`) run without throwing.
- **Content load is the risk surface.** `LoadContent` deserializes textures,
  shaders, sprite fonts, effects. A device that reports no shader caps makes that
  step throw the *same* "pixel shader 1.1" error — which is almost certainly why
  the earlier `NullReference` experiment (see `dwarfs_mod_8769..8771.log`,
  `DeviceType set to NullReference`) was abandoned: it moved the failure from
  device creation into content load rather than removing it.

## Success criteria

A path "works" only if, on the target machine, it lets **N ≫ 2** instances all:
1. boot past device creation (no modal), and
2. reach `reset complete` in their mod log, and
3. step deterministically and return correct obs/reward for a full episode.

Throughput per instance can be lower than a real device — we have many — but
correctness of the sim and obs must be identical.

---

## Path A — Make the NullReference device path actually work

**Idea.** Subscribe to `GraphicsDeviceManager.PreparingDeviceSettings` in
`PrepareGraphics` and set `e.GraphicsDeviceInformation.DeviceType =
DeviceType.NullReference` (D3DDEVTYPE_NULLREF). A NULLREF device allocates almost
nothing on the GPU and renders nothing — exactly what a headless trainer wants.

**Why it stalled before.** NULLREF reports minimal/no caps, so XNA's content
pipeline (or the game's `Effect`/shader load) throws the pixel-shader error
during `LoadContent`, *after* we got past creation. The log trace stops right
after `DeviceType set to NullReference` with no `reset complete`.

**What to try to get it over the line:**
- Pair the NULLREF device with **skipping the content/shader loads that need
  caps** (see Path D). NULLREF only helps if nothing downstream demands shader
  caps. The two paths are complements, not alternatives.
- Check whether the game uses `Effect`/HLSL at all, or just `SpriteBatch` +
  textures. If there are no custom effects, the cap demand may come only from
  `SpriteBatch`'s built-in effect — which still needs PS/VS 1.1. That built-in
  is unavoidable as long as `SpriteBatch` is constructed, even if never drawn.
- Investigate forcing the **Reach** profile and confirm whether NULLREF satisfies
  Reach's minimum on this XNA build, or whether XNA hard-validates against the
  real adapter caps and ignores NULLREF.

**Hook points.** `PrepareGraphics` (subscribe), a `DWARFS_BRIDGE_NULLDEVICE=1`
env switch (loader already references it), `DeviceCreated` log for confirmation.

**Pros.** Smallest conceptual change; per-instance GPU footprint → ~0.
**Cons.** Likely insufficient alone (content load still needs caps); may be
hard-rejected by XNA's validation. Medium effort, uncertain payoff.

## Path B — Reference rasterizer (`DeviceType.Reference`)

**Idea.** Same hook as A, but `DeviceType.Reference`. The ref rasterizer is a
**software** D3D9 device with full caps (it reports PS/VS support), so content
load succeeds.

**Cons.** Needs `d3dref9.dll`, which ships only with the DirectX SDK — **not on
end-user machines**. We'd have to redistribute it (license check) into
`game-patched/`. It's also extremely slow per draw — fine since we suppress Draw,
but device creation/content upload may still be slow. Worth a spike only if A's
"no caps" problem is the sole blocker and we can legally ship the DLL.

## Path C — Many sims, one device (in-process multi-world) ⭐ highest payoff

**Idea.** Stop launching N processes. Launch **one** game process holding **one**
real GPU device, and run **M independent game worlds** inside it, each with its
own bridge port / lockstep mailbox. The device cost is paid once; worlds scale
on CPU/RAM only.

**Why it's attractive.** Sidesteps the GPU ceiling entirely — the thing the logs
say is the actual limit. CPU is what `--power` was really trying to spend anyway.

**Feasibility questions to answer first:**
- Is `Game1` / the sim **instantiable more than once** per process, or does it
  lean on statics / singletons (audio, content manager, global RNG, Steam
  wrapper)? Grep the game for `static` world/level state. Heavy static use kills
  this path or forces `AppDomain` isolation.
- Could we instead run **one `Game1`** but drive M worlds by swapping the active
  level between steps? Probably not — too much shared mutable state per world.
- `AppDomain`-per-world (each loads its own copy of game types) is the .NET 3.5
  way to get isolation in one process, but the GPU device is process-global, so
  multiple devices in one process may hit the *same* ceiling — need to confirm
  whether the limit is per-process or per-device.

**Pros.** If the sim is multi-instantiable, this is the real fix and removes the
GPU as a scaling axis. **Cons.** Largest change; depends entirely on how
static-heavy the game is. Spike the "can `Game1` exist twice?" question before
committing.

**FEASIBILITY CONFIRMED 2026-06-24** (decompiled `Dwarfs.exe` + `DataTypes.dll`
with ilspycmd). The static-heaviness question — the thing that could have killed
this path — comes back clean, and better still the sim turns out to be GPU-free:

- **`Game1` has zero static mutable state.** Its only `static` members are 4 pure
  utility methods (`GetBytes`, `EncodeTo64`, `ParseVector`). All world state —
  `xGameMap` (the `byte[,]`/`ushort[,]` grids), `resources`, `xCity`, `lDwarf`,
  `lEnemy`, `randomizer`, `iGameState`, fade/difficulty — is **instance** state on
  `Game1`. A world *is* a `Game1`.
- **The whole game is nearly static-free.** Across all of `Dwarfs.exe` the only
  static mutable fields are tower-cost config (effectively constants, correctly
  shared) and two enemy-ID counters (`m_iBossIDPool` / `m_iMinionIDPool`, just
  uniqueness). `DataTypes.dll` has no static fields at all. No global "current
  game" singleton, no static map/RNG/content.
- **The simulation is GPU-free.** `UpdateGamefield` (the core per-frame sim, 2295
  lines) has **0** GraphicsDevice/Texture/spriteBatch references; `GenerateLevel`
  is data-only except an *optional* tutorial-tips draw block (guarded by
  `m_bShowTips`). All 2000+ GPU references live in `Draw`/`LoadContent`. `Draw` is
  already suppressed headless.
- **Entry point** (`Program.Main`): `new Game1(steamWrap, steamStats)` then
  `game.Run()`. Steam is passed in, not static.

**Implication — the target shifts from "one device, many worlds" to "many worlds,
ZERO devices."** Since the sim needs no device, the real prize is M `Game1`
instances that never create a graphics device at all, each pumped through
`Update()` in lockstep — removing the GPU as a scaling axis completely (CPU/RAM
bound). The remaining hard part is *not* the game's state model (it's clean) but
the **XNA `Game` lifecycle**: `Game.Run()` does `Initialize`→`LoadContent` (which
creates the real device) and a blocking loop, so a multi-world host has to drive
`Update()` on M instances manually *and* stand in for the device/content that
`Initialize`/`LoadContent` expect (this is where Path D — stub content — merges
in; the sim reads no textures, so stubbing is low-risk, but the few draw-in-sim
spots like the tips block must be guarded).

**Open build risks / next step.** XNA resists being driven without `Run()` and
without a device; the make-or-break spike is **"construct a second `Game1` in the
same process and tick its `Update()` through a `GenerateLevel` + a few frames with
no real device, see where it throws."** Also needs: the mod's `Bridge` made
per-world (today its `phase`/`frame`/mailbox are static — one game assumed; the
reflection helpers already take a `game` arg so they're fine), and the shared
statics neutered (`Input` = human control, unused in training; `Timer` =
profiling; Steam/audio = disable, audio is already null-guarded in `Update`).

**SPIKE RUN 2026-06-24** (`mod/MultiWorldSpike.cs`, gated `DWARFS_BRIDGE_C_SPIKE=1`;
constructs a second `Game1` from the running one's `Update` hook, reusing the
primary's Steam wrappers, and drives it with no device). Result:

```
construct: OK -- a second Game1 exists in-process
Initialize THREW: ContentLoadException: ... "Fonts\fontCodexCategory". GraphicsDevice component not found.
GenerateLevel THREW: NullReferenceException
Update #1 THREW: NullReferenceException
UpdateGamefield (sim only): OK
```

- **A second `Game1` constructs cleanly in one process, and the primary keeps
  running** (it survived the spike). XNA does *not* forbid two `Game` instances —
  the biggest unknown is green.
- **The only device dependency in bring-up is content loading.** `Initialize` dies
  on its first line (`Content.Load<SpriteFont>`) with *"GraphicsDevice component
  not found"* — the `ContentManager` wants an `IGraphicsDeviceService` in
  `game.Services`. That is the entire wall, and it's exactly the Path D surface
  (stub/skip content), not a fundamental blocker.
- **`GenerateLevel`/`Update` threw only as collateral** — `Initialize` aborted, so
  `xGameMap`/`xSoundSystem`/`Input` were never created; plain null cascades, not
  independent device needs.
- **`UpdateGamefield` ran deviceless without throwing** (it no-op'd with no built
  world + `iGameState==0`, but did not crash on a missing device) — consistent
  with the "0 GPU refs in the sim" finding.

**Verdict: Path C's foundation is sound** — two worlds coexist in a process; the
only thing between us and a deviceless world is the content-loading bring-up.

**The build pattern (validated by the spikes below).** Skip the device-coupled
`Initialize`/`LoadContent` on the extra worlds; instead one real `Game1` (the
"host") initializes normally and owns the single device/content, and each extra
world is a `Game1` that **clones the host's fields** (sharing the device, sprite
batch, sound, textures, fonts) but then **reallocates its own per-world state** —
a fresh `xGameMap`, fresh copies of every `List<>` field (the ~25 entity lists),
fresh `xDifficulty`/`xTowerDefense`/`xPlayerInteraction`/`randomizer` — and lets
the game's own `SetDifficulty` → `ClearGame` → `GenerateLevel` build the world.
Per frame, tick the real arcade `Update` sequence: `resources.CheckTheAccount` →
`UpdateGamefield` → `NonSpeedEvents` → `UpdateDynamicMaps` → `Update_CampaignQuests`
→ `resources.BalanceTheAccount`.

**SPIKE PROGRESSION 2026-06-24 → 06-25 (`mod/MultiWorldSpike.cs`, gated
`DWARFS_BRIDGE_C_SPIKE=1`), all run from the primary's `Update` hook:**

- **Single world, full episode.** A second world built this way ran ~15k frames to
  the game's own end state (`gamestate=2`), no crash, with deterministic clock,
  dwarves spawning (0→8) and score climbing (0→4145).
- **Two worlds, rigorous isolation + economy.** Build two worlds with *different*
  seeds and run them interleaved; then prove non-interference by running one seed
  **solo** vs the **same seed paired** with a different-seed world:

  ```
  SOLO  (seed 111, alone)  -> score=1040 dwarves=4 gold=1105 timeLeft=12900 mapFP=274683
  A     (seed 111, paired) -> score=1040 dwarves=4 gold=1105 timeLeft=12900 mapFP=274683
  B     (seed 222, paired) -> score=215  dwarves=4 gold=293  timeLeft=12900 mapFP=238259
  ```

  A (paired) is **byte-identical** to SOLO — score, dwarves, gold, *and* map
  fingerprint — so the second world had **zero** effect on the first. That is
  isolation proven *and* full per-seed determinism (needed for reproducible RL).
  A vs B (different seeds) diverge cleanly. Economy runs (`gold` 250 → 1105).

**Recalibrated status — every "will it work?" unknown is retired with running code:**

| Question | Status |
|---|---|
| >1 `Game1` in one process | ✅ proven |
| Device-free sim runs a full episode | ✅ proven |
| Two worlds **isolated** (zero cross-talk) | ✅ proven (solo == paired, exact) |
| Deterministic per seed | ✅ proven |
| Economy fidelity | ✅ running |
| Enemy/combat path | ⏸ deferred (rare monster caves; needs a long game, not a spike) |
| Obs/reward match the real single-instance env | ✅ verified (1-tick start offset documented) |
| Product: host loop + per-world `Bridge` + Python M-ports | ✅ built + threaded |

**Path C is built — see [MULTIWORLD.md](MULTIWORLD.md) for the production feature.**
The four engineering items below are all **done** (threaded driver, per-world
`World`/`Bridge`, `--multiworld`, correctness gate passing at 10 worlds); kept here
for the record. What's left is *training* readiness (combat validation, parked-world
rebuild, PPO tuning, scaling) — tracked in the repo-root `TODO.md`, not here.

1. ✅ **Bring-up host.** One real `Game1` initializes normally (device owner); the
   extra M-1 worlds are constructed + seeded with the shared infra as above.
   (`DWARFS_BRIDGE_WORLDS=N`; [mod/WorldSim.cs](../mod/WorldSim.cs).)
2. ✅ **Multi-world host + per-world `Bridge`.** Drive M worlds' sim in lockstep, each
   with its own port/mailbox. The static `Bridge` state moved to a per-`Game1`
   `World` ([mod/World.cs](../mod/World.cs)); `Bridge` is now the coordinator. Each
   world runs on its own thread by default (`DWARFS_BRIDGE_SERIAL=1` for the serial
   scheduler).
3. ✅ **Correctness check.** [python/multiworld_test.py](../python/multiworld_test.py)
   proves isolation/divergence/parity at 10 worlds; single-instance `fake_env.py`
   unchanged.
4. ✅ **Python side.** One process exposes M ports; `make_world_env` connects M
   workers to it (`--multiworld`). Removes the GPU as a scaling axis entirely.

## Path D — Skip content/texture/shader loads under headless

**Idea.** Since obs comes from game data and Draw is suppressed, the textures and
effects are dead weight for a trainer. Weave/stub `LoadContent` (or the
`ContentManager.Load<T>` calls) so that under `DWARFS_BRIDGE_HEADLESS` it returns
nulls/placeholders instead of uploading assets — cutting both VRAM and the
cap-demanding shader loads.

**Risk.** The sim may dereference loaded assets (e.g. read a texture's
`Width/Height` for layout, or a sprite sheet for collision rects). Each null
that the *update* path touches is a crash to guard. Requires mapping which loaded
assets `Update`/`GenerateLevel` actually read vs. which only `Draw` reads.

**Pros.** Directly attacks the dominant per-instance VRAM cost and the shader-cap
demand; composes with Path A to make NULLREF viable. **Cons.** Fiddly; lots of
"does the sim touch this asset?" spelunking. Medium-high effort.

## Resolved — already tried or shipped (2026-06-24)

Off the to-try list; kept terse so the conclusions aren't re-derived. The full
teardown of each is in git history.

- **Path E (DXVK d3d9→Vulkan): ruled out for this game.** The "just drop a
  `d3d9.dll` in the game folder" premise is false — it's a .NET process whose
  runtime shim (`mscoreei.dll`) pins DLL resolution to System32
  (`SetDefaultDllDirectories`) and disables DotLocal, so a `d3d9.dll` in the game
  folder, via a `.local` marker, or next to `XnaNative.dll` is always ignored
  (verified with Sysinternals ListDLLs on the live 32-bit process). An in-process
  preload from the mod *does* load DXVK, but XnaNative loads d3d9 via a
  System32-pinned path so XNA binds to the system copy anyway (two `d3d9.dll`
  modules coexist; no `vulkan-1.dll`, no DXVK device). Only replacing
  `SysWOW64\d3d9.dll` machine-wide forces takeover — too invasive to justify
  (d3d9 is shared by Firefox/Steam/BlueStacks, needs protected-folder ACL changes,
  and Steam must stay up for the game to boot). Net: DXVK *might* lift the ceiling
  but there's **no low-risk way to inject it here**; a real attempt needs a
  System32 swap or native `LoadLibraryExW` hooking from the mod.

- **Path F (probe & cap): shipped.** `python/headless_probe.py`
  (`probe_max_instances`) brings instances up one at a time — each on its own port
  clear of training's 8765, keeping prior ones alive so the simultaneous-device
  ceiling is reproduced — and returns the count that all booted and completed a
  RESET (a failed instance connects then freezes on the modal, so its `reset()`
  times out — that's the ceiling signal). Wired into `train.py` as
  **`--power max-safe`** (probes, then caps the run); standalone
  `python python/headless_probe.py --max 8`. The always-on guardrail so a run is
  never poisoned by dead instances while the real headless paths are proven out.

---

## Suggested order of attack

Path C is **built and shipped** ([MULTIWORLD.md](MULTIWORLD.md)) and Path E is
ruled out / Path F is shipped (see Resolved). Path C already removes the GPU as a
scaling axis, so A/B/D are only worth pursuing if you specifically want the
*one-process-per-instance* model to scale (e.g. true process isolation) rather
than the multi-world host. In that case, in order:

1. **Path A + D together** (NULLREF device *and* skip cap-demanding loads) — the
   "stay one-process-per-instance but make the device free" route.
2. **Path B** only if A proves caps are the sole blocker and the DLL is
   redistributable.

## How to measure / a probe harness

- `python/headless_probe.py` exists (Path F): it launches instances one at a time
  and reports the max that come up healthy. To measure A/D, extend it to also log
  per-instance VRAM (via `nvidia-smi` / `dxdiag` / GPU-Z) as each instance is
  added — that turns "does path X raise the ceiling?" into a number.
- Per-instance VRAM before/after is the headline metric for A/D; max-instances is
  the headline for C/E.
- Confirm correctness, not just boot: a probed instance must pass `fake_env.py`
  (full protocol regression) so we know the headless device didn't silently break
  obs/reward.

## Open questions to resolve

- Is the ceiling **per-process** or **per-device**? (Decides whether Path C's
  multiple-devices-in-one-process even helps.)
- Does the game construct any **custom `Effect`/HLSL**, or only `SpriteBatch`?
  (Decides whether NULLREF can ever satisfy content load.)
- Which loaded assets does the **Update/GenerateLevel** path read vs. Draw-only?
  (Scopes Path D.)
- What exact GPU/driver/VRAM is the target machine? (Sanity-checks the "ceiling
  is ~2" assumption and whether it's VRAM vs. device count.)
