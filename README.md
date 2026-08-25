# TABS Agent

An AI-powered army builder for Totally Accurate Battle Simulator. A C# mod reads real game state and places units, while a Python LangChain LLM builds the plan.

## How It Works

1. C# mod writes `state.json` to disk with enemy army info and budget
2. Python reads it, calls the LLM, writes `plan.json` with unit placements
3. C# mod polls `plan.json` and spawns units in-game

Default IO directory: `%LOCALAPPDATA%\TabsAgentModIO\`

## Project Layout

```
TabsAgentMod/           C# BepInEx plugin (reads state, applies plan)
core/                   Python strategy logic
  strategy_llm.py       LangChain prompt + structured output
  models.py             Domain models (Unit, Army, GameState)
  unit_catalog.py       Load unit data from JSON
data/
  units_dummy.json      Unit catalog placeholder
run_llm_file_bridge.py  Python bridge (state.json -> plan.json)
```

## Prerequisites

1. [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases)
2. [Visual Studio 2022](https://visualstudio.microsoft.com/downloads/)
3. [.NET Framework 4.7.2 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net472)
4. Python 3.10+
5. [ILSpy](https://github.com/icsharpcode/ILSpy/releases) (optional, for exploring game assemblies)

## Installation

1. Unzip BepInEx into your TABS folder:
   `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator`

2. Run TABS once, then exit (creates the `BepInEx/` folder structure)

3. Open `TabsAgentMod/TabsAgentMod.csproj` in Visual Studio and build

4. Copy `TabsAgentMod.dll` to:
   `...\Totally Accurate Battle Simulator\BepInEx\plugins\`

5. Run TABS once, then exit (generates `BepInEx/config/com.tabsagent.mod.cfg`)

6. In that config file, under `[LLM]`, set `BridgeScriptPath` to the full path of
   `run_llm_file_bridge.py` on your machine (and `PythonExePath` too, if `python` isn't
   on your PATH). This is what lets the mod launch the bridge itself when you press P
   in-game, instead of you having to run it manually in a terminal.

7. Run TABS and confirm `BepInEx/LogOutput.log` shows `"TabsAgent Mod loaded."`

## Python Setup

```bash
pip install -r requirements.txt
```

Set your Anthropic API key in a `.env` file:
```
ANTHROPIC_API_KEY=sk-ant-...
```

## Usage

The Python bridge watches for `state.json` and generates `plan.json` automatically.

```bash
# Continuous watch mode (default)
python run_llm_file_bridge.py

# Single run mode
python run_llm_file_bridge.py --once

# Custom IO directory
python run_llm_file_bridge.py --io-dir "C:\path\to\io"
```
