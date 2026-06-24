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

Path E is ruled out and Path F is shipped (see Resolved). Remaining, in order:

1. **Path C feasibility** (can `Game1` exist twice / how static-heavy is the
   sim?) — the highest-ceiling fix worth pursuing; answer the instantiability
   question before investing anywhere else big.
2. **Path A + D together** (NULLREF device *and* skip cap-demanding loads) — the
   "stay one-process-per-instance but make the device free" route.
3. **Path B** only if A proves caps are the sole blocker and the DLL is
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
