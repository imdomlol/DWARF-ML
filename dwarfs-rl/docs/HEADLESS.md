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

## Path E — DXVK / d3d9-to-Vulkan translation layer

**Idea.** Drop a `d3d9.dll` (DXVK) next to `game-patched\Dwarfs.exe` so D3D9
calls run on Vulkan. Vulkan's per-process device model may lift the concurrent
D3D9 ceiling, and DXVK is far more tolerant of many contexts.

**Pros.** Possibly zero code change — just a DLL in the output folder; the loader
already lays out `game-patched/`. **Cons.** Adds a redistributable dependency and
a moving part; behavior on this exact XNA 3.1 title is unknown; may or may not
change the ceiling. Cheap to **spike** (drop the DLL, launch 4), so worth an
early empirical test even though it's inelegant.

## Path F — Just probe & cap (not headless, but stops the bleeding)

**Idea.** Not a true-headless path, but the safety net: a one-time preflight that
launches instances until one fails, caches the max that fit, and caps
`make_vec_env` to it so a run is never poisoned by dead instances. Recover lost
throughput with higher `action_repeat`.

**Use it as.** The fallback we ship regardless, so training is robust while the
real headless paths (A/C/D) are still being proven out.

---

## Suggested order of attack

1. **Path E spike** (DXVK drop-in) — an afternoon, possibly fixes it for free.
2. **Path C feasibility** (can `Game1` exist twice / how static-heavy is the
   sim?) — this is the highest-ceiling fix; answer the instantiability question
   before investing anywhere else big.
3. **Path A + D together** (NULLREF device *and* skip cap-demanding loads) — the
   "stay one-process-per-instance but make the device free" route.
4. **Path F** as the always-on guardrail.
5. **Path B** only if A proves caps are the sole blocker and the DLL is
   redistributable.

## How to measure / a probe harness

- Add a tiny `python/headless_probe.py`: launch instances one at a time with a
  chosen device mode env var, wait for each mod log to reach `reset complete`,
  and report the max that come up + per-instance VRAM (via `nvidia-smi` /
  `dxdiag` / GPU-Z). This turns "does path X raise the ceiling?" into a number.
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
