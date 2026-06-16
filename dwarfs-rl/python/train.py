import argparse
import os

import torch
from stable_baselines3 import PPO

from dwarfs_env import DwarfsEnv


def resolve_power(level: str):
	# Bundles "how much of the machine to use" into one dial. Returns a default
	# instance count, a per game frame cap (0 = uncapped/full speed, a low cap
	# makes each game sleep between frames so the rest of the PC stays usable),
	# and how many CPU threads PyTorch may use for inference so it does not fight
	# the game processes for cores. An explicit --instances overrides the count.
	logical = os.cpu_count() or 4
	physical = max(1, logical // 2)  # assume SMT, leave the trainer some room
	if level == "max":
		return max(1, physical - 1), 0, min(4, logical)
	if level == "min":
		return 1, 15, 1
	# moderate
	return max(1, physical // 2), 0, 2


def train_agent(total_timesteps: int, render: bool = False, render_fps: int = 0,
		instances: int = None, power: str = None) -> None:
	# The mod connects over websocket and provides observations/rewards.
	# One instance attaches to a manually launched game; more than one spins up
	# that many game processes and trains across all of them at once.
	throttle_fps = 0
	if power is not None:
		power_instances, throttle_fps, torch_threads = resolve_power(power)
		torch.set_num_threads(torch_threads)
		if instances is None:
			instances = power_instances
		cap = "uncapped" if throttle_fps == 0 else f"{throttle_fps} fps"
		print(f"Power '{power}': {instances} instance(s), {cap} per game, "
			f"{torch_threads} torch thread(s).")
	if instances is None:
		instances = 1

	# The frame cap only paces when we are actually drawing. Headless, the pace
	# comes from the power throttle (0 = full speed). Passing the render cap to a
	# headless run would silently throttle training, so keep the two separate.
	pace_fps = render_fps if render else throttle_fps

	if instances > 1:
		from dwarfs_env import make_vec_env
		env = make_vec_env(instances, render=render, render_fps=pace_fps)
	else:
		env = DwarfsEnv(render=render, render_fps=pace_fps)
	model = PPO("MultiInputPolicy", env, verbose=1, learning_rate=0.0003)

	print("Training the AI...")
	model.learn(total_timesteps=total_timesteps)
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
	parser.add_argument("--power", choices=("max", "moderate", "min"), default=None,
		help="How much of the machine to use. max = as many instances as fit, full speed; "
			"moderate = about half the cores, full speed; min = one throttled instance to stay out "
			"of the way. Sets the instance count (unless --instances is given), a per game frame cap, "
			"and PyTorch's thread count.")
	return parser


def main() -> None:
	args = build_parser().parse_args()
	if args.mode == "demo":
		demo_run(args.steps, render=args.render, render_fps=args.render_fps)
	else:
		train_agent(args.timesteps, render=args.render, render_fps=args.render_fps,
			instances=args.instances, power=args.power)


if __name__ == "__main__":
	main()
