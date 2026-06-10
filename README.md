# DWARF-ML

Machine-learning training on Dwarfs!?.

## Overview

DWARF-ML is about teaching a machine to play the game Dwarfs!? by making use of a game mod that shares game state information with the machine while training.

## Repository

- [dwarf_mod_env.py](/home/dom/GitHub/dwarf-ml/dwarf_mod_env.py) — Utilities for model/environment setup.
- [train.py](/home/dom/GitHub/dwarf-ml/train.py) — Main training script
- [mod_specs.md](/home/dom/GitHub/dwarf-ml/mod_specs.md) — Game mod notes and specs.
- [requirements.txt](/home/dom/GitHub/dwarf-ml/requirements.txt) — Python dependencies

## Quickstart

1. Create and activate a virtual environment:

```bash
python3 -m venv .venv
source .venv/bin/activate
```

2. Install dependencies:

```bash
pip install -r requirements.txt
```

3. Run a training run (WIP):

```bash
python train.py
```
