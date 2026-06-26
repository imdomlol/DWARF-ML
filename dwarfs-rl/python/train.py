import argparse
import os

import torch
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.monitor import Monitor
from stable_baselines3.common.vec_env import VecMonitor

from dwarfs_env import DwarfsEnv


class GameMetricsCallback(BaseCallback):
    """logs Dwarfs-specific end-of-episode numbers (final score) on top of the
    reward/length curves VecMonitor already records. score is the actual game
    objective, so it's the most interpretable 'is it learning' signal -- shaped
    reward can drift while score is what you care about."""

    def __init__(self):
        super().__init__()
        self._scores = []

    def _on_step(self) -> bool:
        # Monitor stamps info["episode"] on the step an episode ends; the env also
        # carries the live "score" each step, so at that step it's the final score
        for info in self.locals.get("infos", []):
            if "episode" in info and "score" in info:
                self._scores.append(float(info["score"]))
        return True

    def _on_rollout_end(self) -> None:
        if self._scores:
            n = len(self._scores)
            self.logger.record("game/final_score_mean", sum(self._scores) / n)
            self.logger.record("game/final_score_max", max(self._scores))
            self.logger.record("game/episodes_in_rollout", n)
            self._scores.clear()


def resolve_power(level: str):
	# Bundles "how much of the machine to use" into one dial. Returns a default
	# instance count, a per game frame cap (0 = uncapped/full speed, a low cap
	# makes each game sleep between frames so the rest of the PC stays usable),
	# and how many CPU threads PyTorch may use for inference so it does not fight
	# the game processes for cores. An explicit --instances overrides the count.
	logical = os.cpu_count() or 4
	physical = max(1, logical // 2)  # assume SMT, leave the trainer some room
	if level in ("max", "max-safe"):
		return max(1, physical - 1), 0, min(4, logical)
	if level == "min":
		return 1, 15, 1
	# moderate
	return max(1, physical // 2), 0, 2


def train_agent(total_timesteps: int, render: bool = False, render_fps: int = 0,
		instances: int = None, power: str = None, invalid_action: float = 0.1,
		multiworld: bool = False, tensorboard: bool = False, logdir: str = "./runs") -> None:
	# The mod connects over websocket and provides observations/rewards.
	# One instance attaches to a manually launched game; more than one spins up
	# that many game processes and trains across all of them at once.
	throttle_fps = 0
	if power is not None:
		power_instances, throttle_fps, torch_threads = resolve_power(power)
		torch.set_num_threads(torch_threads)
		if instances is None:
			instances = power_instances
		if power == "max-safe" and not multiworld:
			# preflight: bring instances up one at a time and cap to how many the
			# GPU actually allows, so the run isnt poisoned by ones that fail
			# device creation. see headless_probe.py / docs/HEADLESS.md.
			# multi-world shares one device, so there's no per-instance ceiling to
			# probe -- it's skipped below
			from headless_probe import probe_max_instances
			print(f"Power 'max-safe': probing up to {instances} instance(s) to see "
				f"how many boot on this GPU (one-time preflight, launches games)...")
			safe = probe_max_instances(instances, invalid_action=invalid_action)
			if safe < 1:
				raise RuntimeError(
					"max-safe probe: no game instance booted. check the loader verify "
					"line and that game-patched\\Dwarfs.exe runs on its own.")
			if safe < instances:
				print(f"Power 'max-safe': only {safe} of {instances} came up; capping to {safe}.")
			instances = safe
		cap = "uncapped" if throttle_fps == 0 else f"{throttle_fps} fps"
		print(f"Power '{power}': {instances} instance(s), {cap} per game, "
			f"{torch_threads} torch thread(s).")
	if instances is None:
		instances = 1

	# The frame cap only paces when we are actually drawing. Headless, the pace
	# comes from the power throttle (0 = full speed). Passing the render cap to a
	# headless run would silently throttle training, so keep the two separate.
	pace_fps = render_fps if render else throttle_fps

	# invalid_action docks the reward whenever the game refuses a move, so the
	# agent stops spamming walls/arrows where they cant go. 0 turns it off.
	if invalid_action:
		print(f"Invalid action penalty: {invalid_action} per refused move.")

	if instances > 1 and multiworld:
		# one game process, N worlds in it, sharing one GPU device. scales on
		# CPU/RAM, not the graphics card (docs/MULTIWORLD.md)
		from dwarfs_env import make_world_env
		print(f"Multi-world: one process hosting {instances} worlds on ports "
			f"8765..{8765 + instances - 1} (one shared GPU device).")
		env = make_world_env(instances, render=render, render_fps=pace_fps,
			invalid_action=invalid_action)
		env = VecMonitor(env)
	elif instances > 1:
		from dwarfs_env import make_vec_env
		env = make_vec_env(instances, render=render, render_fps=pace_fps,
			invalid_action=invalid_action)
		env = VecMonitor(env)
	else:
		# Monitor records episode reward/length so they show up in the logs;
		# without it SB3 only prints fps/loss and you can't see learning at all
		env = Monitor(DwarfsEnv(render=render, render_fps=pace_fps,
			invalid_action=invalid_action))

	# tensorboard_log makes SB3 write event files; view with
	# `tensorboard --logdir <logdir>` then open http://localhost:6006
	tb_log = logdir if tensorboard else None
	if tensorboard:
		print(f"TensorBoard logging to {logdir} -- run: tensorboard --logdir {logdir}")
	model = PPO("MultiInputPolicy", env, verbose=1, learning_rate=0.0003,
		tensorboard_log=tb_log)

	print("Training the AI...")
	model.learn(total_timesteps=total_timesteps, callback=GameMetricsCallback(),
		tb_log_name="ppo")
	print("Training complete.")

	print("Saving the AI...")
	model.save("dwarfs_agent")
	print("Model saved.")

	env.close()



def demo_run(steps: int, render: bool = True, render_fps: int = 60) -> None:
	# Demo mode is usually easier to follow with rendering on.
	env = DwarfsEnv(render=render, render_fps=render_fps)
	observation, info = env.reset()
	print("Demo run started. If the mod is open, you should see the game state update there.")
	for step_index in range(steps):
		action = env.action_space.sample()
		observation, reward, terminated, truncated, info = env.step(action)
		print(
			f"step={step_index + 1} action={action} reward={reward:.3f} "
			f"terminated={terminated} truncated={truncated}"
		)
		if terminated or truncated:
			observation, info = env.reset()
	env.close()


def build_parser() -> argparse.ArgumentParser:
	parser = argparse.ArgumentParser(description="Train or demo the websocket-backed Dwarfs agent.")
	parser.add_argument("--mode", choices=("train", "demo"), default="train")
	parser.add_argument("--timesteps", type=int, default=5000, help="Training timesteps when mode=train.")
	parser.add_argument("--steps", type=int, default=25, help="Steps to run when mode=demo.")
	parser.add_argument("--render", action="store_true", help="Enable game rendering during training or demo.")
	parser.add_argument("--render-fps", type=int, default=60, help="Render frame cap when --render is enabled.")
	parser.add_argument("--instances", type=int, default=None,
		help="Game instances to train across at once. Defaults to 1, or the power level's pick.")
	parser.add_argument("--power", choices=("max", "max-safe", "moderate", "min"), default=None,
		help="How much of the machine to use. max = as many instances as cores allow, full speed; "
			"max-safe = like max but first probes how many game instances actually boot on this GPU "
			"and caps to that (slower startup, but a run is never poisoned by instances that fail "
			"device creation); moderate = about half the cores, full speed; min = one throttled "
			"instance to stay out of the way. Sets the instance count (unless --instances is given), "
			"a per game frame cap, and PyTorch's thread count.")
	parser.add_argument("--invalid-action", type=float, default=0.1,
		help="Reward penalty per refused action (illegal placement, not enough gold, etc). "
			"On by default to discourage spamming impossible moves; pass 0 to turn it off.")
	parser.add_argument("--multiworld", action="store_true",
		help="Run all instances as worlds inside ONE game process sharing one GPU "
			"device (Path C), instead of one process per instance. Lets the instance "
			"count scale past the GPU's 2-3 device ceiling onto CPU/RAM. Skips the "
			"max-safe device probe (there's no per-instance device to cap).")
	parser.add_argument("--tensorboard", action="store_true",
		help="Write TensorBoard logs (episode reward/length, losses, fps, and game "
			"score) to --logdir. View with: tensorboard --logdir ./runs")
	parser.add_argument("--logdir", default="./runs",
		help="Directory for TensorBoard logs (default ./runs).")
	return parser


def main() -> None:
	args = build_parser().parse_args()
	if args.mode == "demo":
		demo_run(args.steps, render=args.render, render_fps=args.render_fps)
	else:
		train_agent(args.timesteps, render=args.render, render_fps=args.render_fps,
			instances=args.instances, power=args.power, invalid_action=args.invalid_action,
			multiworld=args.multiworld, tensorboard=args.tensorboard, logdir=args.logdir)


if __name__ == "__main__":
	main()
