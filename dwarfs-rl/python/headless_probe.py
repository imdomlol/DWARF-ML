"""Path F - probe & cap (see docs/HEADLESS.md).

Spinning up N game instances fails for the extra ones once the GPU/driver can't
hand out another D3D9 device ("No suitable graphics card found..."), and the
ceiling moves with the machine (an RX 5700 takes ~2-3, an RTX 5070 ~6-7). Until
the real headless paths (one-device-many-worlds, NULLREF, DXVK) are proven out,
this is the always-on guardrail: a one-time preflight that brings instances up
one at a time until one fails, so training can be capped to what actually boots
and a run is never poisoned by dead instances.

Used by train.py for --power max-safe, and runnable standalone:
    python python/headless_probe.py --max 8
"""

import argparse
import time

from dwarfs_env import DwarfsEnv, default_game_exe


def probe_max_instances(max_try, game_exe=None, base_port=8900, reset_timeout=10.0,
                        verbose=True, **env_kwargs):
    """Bring instances up one at a time, keeping every prior one alive, until one
    fails to boot and RESET. Returns the largest count that all came up healthy
    together - the GPU/driver ceiling on this machine.

    Keeping the earlier games running while the next launches is the point: the
    failure is about devices coexisting, not creation timing (docs/HEADLESS.md),
    so a real probe has to hold them all open at once. A failed instance still
    *connects* (the mod attaches at Boot, before the device is created) but then
    freezes on the device-creation modal, so its RESET never gets a reply and
    reset() times out after reset_timeout - that's how we detect the ceiling.
    reset_timeout only needs to clear a healthy boot (a few seconds); a slow-but-
    real instance timing out just caps us one low, which is the safe direction.

    Runs on its own port range (base_port, well clear of training's 8765) and
    leaves its server threads parked for the rest of the process. It's a one-shot
    preflight, so that small leak is cheaper than teaching the lockstep bridge to
    shut its asyncio server down mid-flight.
    """
    if game_exe is None:
        game_exe = default_game_exe()
    if max_try < 1:
        return 0

    envs = []
    healthy = 0
    try:
        for i in range(max_try):
            port = base_port + i
            if verbose:
                print(f"[probe] booting instance {i + 1}/{max_try} (port {port}), "
                      f"{healthy} already up...", flush=True)
            started = time.time()
            env = DwarfsEnv(port=port, game_exe=game_exe, **env_kwargs)
            envs.append(env)
            try:
                env.reset(timeout=reset_timeout)
            except Exception as exc:
                if verbose:
                    print(f"[probe]   instance {i + 1} did not come up "
                          f"({type(exc).__name__}); ceiling is {healthy}.", flush=True)
                break
            healthy = i + 1
            if verbose:
                print(f"[probe]   ok in {time.time() - started:.0f}s "
                      f"({healthy} healthy together).", flush=True)
    finally:
        for env in envs:
            try:
                env.close()
            except Exception:
                pass
    return healthy


def main():
    parser = argparse.ArgumentParser(
        description="Probe how many patched-game instances boot together on this machine.")
    parser.add_argument("--max", type=int, default=8,
                        help="Upper bound to probe up to.")
    parser.add_argument("--base-port", type=int, default=8900,
                        help="Port range to probe on (kept clear of training's 8765).")
    parser.add_argument("--reset-timeout", type=float, default=10.0,
                        help="Seconds to wait for each instance to RESET before calling it failed.")
    args = parser.parse_args()
    n = probe_max_instances(args.max, base_port=args.base_port,
                            reset_timeout=args.reset_timeout)
    print(f"\nMax instances that booted healthy together: {n}")


if __name__ == "__main__":
    main()
