# TABS Agent

An RL agent that learns army compositions for Totally Accurate Battle Simulator.
A C# BepInEx mod exposes real game state over a file bridge; Python drives training
against it with PPO (stable-baselines3) and CMA-ES (pycma).

## How It Works

The mod and Python talk through JSON files in a shared IO directory:

| File | Written by | Purpose |
|---|---|---|
| `state.json` | mod | current game state (enemy army, budget, phase, outcome) |
| `plan.json` | Python | unit placements to apply |
| `reset.json` | Python | request a level reset |
| `start_battle.json` | Python | request battle start |

The mod writes `state.json` and polls `plan.json` on timers, so Python drives an
episode by writing `reset.json` → `plan.json` → `start_battle.json` and reading the
resulting `state.json`. This is the `TabsEnv` loop in `core/env.py`.

Default IO directory: `%LOCALAPPDATA%\TabsAgentModIO\`

## Project Layout

```
TabsAgentMod/           C# BepInEx plugin (writes state, applies plans)
core/
  env.py                TabsEnv — file-bridge episode loop and reward
  ppo_env.py            Gymnasium wrapper over TabsEnv
  train_ppo.py          PPO training entry point
  ga_search.py          CMA-ES search over army compositions
  models.py             Unit dataclass
  unit_catalog.py       Loads the unit catalog; filters out units the game can't place
data/
  units_dummy.json      Unit names and costs the search budgets against
RUNBOOK.md              Day-to-day operation: starting, checking, tuning, archiving
```

## Prerequisites

1. [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases)
2. Python 3.12
3. [ILSpy](https://github.com/icsharpcode/ILSpy/releases) (optional, for exploring game assemblies)

Building the C# mod is only needed if you change it. Note that `TabsAgentMod/bin/`
is gitignored, so a fresh clone has no prebuilt DLL and must build one. To build,
you need the [.NET SDK](https://dotnet.microsoft.com/download) (`dotnet build`) — full
Visual Studio is not required. The csproj already references
`Microsoft.NETFramework.ReferenceAssemblies`, so the .NET Framework Developer Pack isn't needed.

## Mod Installation

1. Unzip BepInEx into your TABS folder:
   `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator`

2. Run TABS once, then exit (creates the `BepInEx/` folder structure)

3. Copy `TabsAgentMod.dll` to:
   `...\Totally Accurate Battle Simulator\BepInEx\plugins\`

4. Run TABS once, then exit (generates `BepInEx/config/com.tabsagent.mod.cfg`)

5. Run TABS and confirm `BepInEx/LogOutput.log` shows `"TabsAgent Mod loaded."`

## Python Setup

```bash
py -3.12 -m venv .venv
.venv\Scripts\activate

# torch must come first, from the CUDA 12.8 index — see note below
pip install torch --index-url https://download.pytorch.org/whl/cu128
pip install -r requirements.txt
```

### GPU note

On Blackwell cards (RTX 50-series, `sm_120`) the default PyPI torch wheel fails at
runtime with *"no kernel image is available for execution on the device"*. Install
torch from the `cu128` index as shown above.

### Smart App Control note

If Windows Smart App Control is enabled, recent numpy releases can fail to import
with *"An Application Control policy has blocked this file"* — the wheels are
unsigned and new versions have no reputation yet. `requirements.txt` pins
`numpy==2.2.6` for this reason.

## Usage

Training drives the game directly, so TABS must be running with a campaign level
loaded and in the placement phase.

```bash
python -m core.train_ppo    # PPO
python -m core.ga_search    # CMA-ES
```

### In-game keys

- **R** — reset the level
- **B** — start the battle

Both are also triggerable from Python via `reset.json` / `start_battle.json`.
