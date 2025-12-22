# TABS Agent (mod-based)

Goal: a TABS mod reads real game state and places units, while a Python LangChain
LLM builds the plan. No screen capture.

## How it works
- C# mod writes `state.json` to disk.
- Python reads it, calls the LLM, writes `plan.json`.
- C# mod polls `plan.json` and applies the placements in-game.

Default IO directory:
`%LOCALAPPDATA%\TabsAgentModIO\`

## Project layout
- `TabsAgentMod/`: C# BepInEx plugin (reads state, applies plan)
- `run_llm_file_bridge.py`: Python bridge (state.json -> plan.json)
- `core/strategy_llm.py`: LangChain prompt + structured plan
- `core/unit_catalog.py`: load unit data
- `data/units_dummy.json`: unit catalog placeholder
- `docs/CSharpCrashCourse.md`: C# install + build steps

## Download order (do this first)
1) BepInEx 5 (x64): https://github.com/BepInEx/BepInEx/releases
2) Visual Studio 2022: https://visualstudio.microsoft.com/downloads/
3) .NET Framework 4.7.2 Developer Pack: https://dotnet.microsoft.com/download/dotnet-framework/net472
4) ILSpy (assembly browser): https://github.com/icsharpcode/ILSpy/releases
5) UnityExplorer (optional): https://github.com/sinai-dev/UnityExplorer/releases

## Install order (what to do with the downloads)
1) Unzip BepInEx into:
   `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator`
2) Run TABS once, then exit. This creates `BepInEx/`.
3) Open `TabsAgentMod/TabsAgentMod.csproj` in Visual Studio.
4) Implement the C# stubs in `TabsAgentMod/` (see `docs/Plan.md`).
5) Build the project, then copy `TabsAgentMod.dll` to:
   `...\Totally Accurate Battle Simulator\BepInEx\plugins\`
6) Run TABS and confirm `BepInEx/LogOutput.log` shows the mod loaded.
7) In a terminal, run: `python run_llm_file_bridge.py`

## File plan (what to implement first)
1) `TabsAgentMod/Models.cs`:
   - Define the JSON data model (state + plan payloads).
2) `TabsAgentMod/ModConfig.cs`:
   - Bind config values (IO directory, polling, mock mode).
3) `TabsAgentMod/FileBridge.cs`:
   - Read/write JSON files and detect changes.
4) `TabsAgentMod/Plugin.cs`:
   - Wire the loop that writes state and reads plans.
5) `TabsAgentMod/Collectors.cs`:
   - Mock collector first, then real game state.
6) `TabsAgentMod/PlanApplier.cs`:
   - Convert placements into in-game spawns.

For more detail, open `docs/CSharpCrashCourse.md`.
For the step-by-step plan, open `docs/Plan.md`.
