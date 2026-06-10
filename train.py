import argparse

from stable_baselines3 import PPO

from dwarf_mod_env import LiveDwarfsEnv


def train_agent(total_timesteps: int) -> None:
	# The mod is expected to connect over websocket and provide observations/rewards.
	env = LiveDwarfsEnv()
	model = PPO("MlpPolicy", env, verbose=1, learning_rate=0.0003)

	print("Training the AI...")
	model.learn(total_timesteps=total_timesteps)
	print("Training complete.")

	print("Saving the AI...")
	model.save("live_dwarfs_agent")
	print("Model saved.")

	env.close()



def demo_run(steps: int) -> None:
	# This does not render from Python; the mod should show its own window if visual mode is enabled there.
	env = LiveDwarfsEnv()
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
	parser.add_argument("--timesteps", type=int, default=50000, help="Training timesteps when mode=train.")
	parser.add_argument("--steps", type=int, default=25, help="Steps to run when mode=demo.")
	return parser


def main() -> None:
	args = build_parser().parse_args()
	if args.mode == "demo":
		demo_run(args.steps)
	else:
		train_agent(args.timesteps)


if __name__ == "__main__":
	main()
