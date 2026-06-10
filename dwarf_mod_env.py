"""
This is a gymnasium environment that connects to a modded Dwarfs!? instance via a WebSocket server. 
The mod sends live game state data as JSON, which is parsed into a structured observation for the AI
to learn from. The AI's actions are sent back to the mod in real-time, allowing for interactive training 
sessions. This setup enables the use of reinforcement learning algorithms to train an agent to play 
Dwarfs!? by directly interfacing with the game's internal state and mechanics.

The mod is expected to be running and connect to this server before training or demoing the agent. The 
mod should handle the game logic, rendering, and reward calculations, while this environment focuses on 
the communication and data parsing aspects to facilitate the AI's learning process.
"""

import gymnasium as gym
from gymnasium import spaces
import numpy as np
import json
import asyncio
import websockets
from threading import Thread

class LiveDwarfsEnv(gym.Env):
    """Custom Gymnasium environment for live interaction with a Dwarfs!? mod via WebSocket."""

    def __init__(self):
        super().__init__()
        # Visuals are avoided here this is purely numerical data for the AI to learn from
        self.observation_space = spaces.Box(low=0, high=4, shape=(40, 60), dtype=np.int32)
        self.action_space = spaces.Discrete(3) # 0=Idle, 1=Place Dynamite, 2=Place Wall, etc WIP
        
        # Manage the live network connection to the game
        self.current_state = None
        self.loop = asyncio.new_event_loop()
        self.server_thread = Thread(target=self._start_server, daemon=True)
        self.server_thread.start()

    def _start_server(self):
        """Start the WebSocket server to listen for incoming connections from the game mod."""

        asyncio.set_event_loop(self.loop)
        start_server = websockets.serve(self._handler, "localhost", 8765)

        try:
            self.server = self.loop.run_until_complete(start_server)
        except Exception as e:
            print(f"Failed to start WebSocket server: {e}")
            return 0

        try:
            self.loop.run_forever()
        except Exception as e:
            print(f"WebSocket server loop stopped with error: {e}")
            return 0
        
        return 1

    async def _handler(self, websocket, path):
        """Handle incoming WebSocket messages."""

        self.websocket = websocket
        async for message in websocket:
            self.current_state = json.loads(message)

    def _parse_observation(self, state):
        """Convert the raw state data from the mod into a structured observation for the model."""

        return np.array(state["map_grid"]).reshape(40, 60)

    def reset(self, seed=None, options=None):
        """Reset the environment to an initial state and return the initial observation."""

        # Require websocket connection
        if not hasattr(self, 'websocket'):
            return None, {}

        # Tell the game mod to wipe the board and start a new match seed
        asyncio.run_coroutine_threadsafe(self.websocket.send(json.dumps({"command": "RESET"})), self.loop)
        
        # Wait until the mod returns the first clean frame data pack
        while self.current_state is None: pass
        
        # Parse the initial frame data into an observation grid
        obs = self._parse_observation(self.current_state)

        return obs, {}

    def step(self, action):
        """Send the AI's action to the game mod and wait for the updated state to return."""

        # Send the AI's action to mod
        action_packet = {"command": "STEP", "action": int(action)}
        asyncio.run_coroutine_threadsafe(self.websocket.send(json.dumps(action_packet)), self.loop)
        
        # Wait for the game mod to process the tick and return the updated frame state
        while self.current_state is None: pass
        state = self.current_state
        self.current_state = None # Clear it out for the next frame tick
        
        # Parse the state data into the observation, reward, and done flags for the AI
        obs = self._parse_observation(state)
        reward = float(state["immediate_reward"])
        terminated = bool(state["terminated"])
        truncated = bool(state["truncated"])
        
        return obs, reward, terminated, truncated, {}
