"""runs a random agent for a bunch of episodes through the gym env.

partly a demo of DwarfsEnv and partly a soak test, lots of episodes back to
back shakes out anything that only breaks after the tenth reset. start this
then launch the patched game.
"""

import sys
import time

from dwarfs_env import DwarfsEnv

EPISODES = int(sys.argv[1]) if len(sys.argv) > 1 else 8


def main():
    env = DwarfsEnv(action_repeat=8)
    print(f"running {EPISODES} random episodes, waiting for the game...")

    t0 = time.perf_counter()
    for ep in range(EPISODES):
        obs, info = env.reset()
        total = 0.0
        steps = 0
        placed = 0
        while True:
            action = env.action_space.sample()
            obs, reward, terminated, truncated, info = env.step(action)
            total += reward
            steps += 1
            if info["action_ok"] and action[0] != 0:
                placed += 1
            if terminated or truncated:
                break
        print(f"ep {ep + 1}: {steps} steps, reward {total:.0f}, "
              f"score {info['score']}, {placed} things placed, "
              f"gold left {int(obs['stats'][0])}")

    dt = time.perf_counter() - t0
    print(f"{EPISODES} episodes in {dt:.0f}s "
          f"({dt / EPISODES:.1f}s each), no crashes")
    env.close()


if __name__ == "__main__":
    main()
