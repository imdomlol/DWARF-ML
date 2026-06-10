# Dwarfs!? Mod Specifications

## WebSocket Connection

The mod must connect to the gymnasium environment via WebSocket on `localhost:8765`.

## Message Format

### Messages from Mod to Environment

The mod sends JSON messages with the following structure (WIP):

```json
{
  "map_grid": [0, 1, 2, ...],
  "immediate_reward": 5.0,
  "terminated": false,
  "truncated": false
}
```

**Fields:**
- **`map_grid`** (array of integers): The game state grid flattened to 1D (1 = empty 2 = rock, etc)
- **`immediate_reward`** (float): The reward obtained from the last action. Can be positive, negative, or zero.
- **`terminated`** (boolean): Whether the episode ended in a terminal state (game over—win, loss, or failure). If `true`, the episode is done.
- **`truncated`** (boolean): Whether the episode was cut short for another reason (e.g., max steps, time limit). If `true`, the episode is also done.

### Commands from Environment to Mod

The environment sends JSON commands:

#### RESET Command
```json
{
  "command": "RESET"
}
```
Response: The mod must reset the game state and return the first clean frame as a message (see format above).

#### STEP Command
```json
{
  "command": "STEP",
  "action": 0
}
```
**Action values (WIP):**
- `0`: Idle (do nothing)
- `1`: Place dynamite
- `2`: Place Wall

Response: The mod must process the action, update the game state, and return the updated frame as a message (see format above).

## Observation Space

- **Shape**: WIP (depends on gamemode i think)
- **Data type**: int32
- **Value range**: (represents different cell types)

## Action Space

- **Type**: Discrete
- **Actions**: 3 (0=Idle, 1=Place Dynamite, 2=Place Wall)

## Notes

- The mod **must** include all fields (`map_grid`, `immediate_reward`, `terminated`, `truncated`, etc) in every message.
- The environment will busy-wait for the next message after sending a command
