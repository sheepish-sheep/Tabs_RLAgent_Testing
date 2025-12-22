# Plan (mod-based, file bridge)

This is the step-by-step build order for the mod + Python LLM flow.

## Phase 0: downloads and installs
1) Install Visual Studio 2022 with ".NET desktop development".
2) Install .NET Framework 4.7.2 Developer Pack.
3) Install BepInEx into the TABS folder.
4) Unzip ILSpy somewhere convenient.
5) (Optional) Install UnityExplorer into `BepInEx/plugins/`.

## Phase 1: implement the data model + config
1) `TabsAgentMod/Models.cs`
   - Add fields for UnitCount, StatePayload, Placement, PlanPayload.
   - Choose a JSON serializer and annotate fields if needed.
2) `TabsAgentMod/ModConfig.cs`
   - Bind IO directory, poll intervals, mock mode, mock budget.

## Phase 2: implement file IO
1) `TabsAgentMod/FileBridge.cs`
   - Create IO directory.
   - Write `state.json`.
   - Read `plan.json` only when it changes.

## Phase 3: wire the plugin loop
1) `TabsAgentMod/Plugin.cs`
   - Initialize config, file bridge, collector, applier.
   - Add timers for write and poll.
2) `TabsAgentMod/Collectors.cs`
   - Implement `MockStateCollector` so the pipeline works.

## Phase 4: verify the pipeline (mock data)
1) Build `TabsAgentMod/TabsAgentMod.csproj`.
2) Copy `TabsAgentMod.dll` into:
   `...\Totally Accurate Battle Simulator\BepInEx\plugins\`
3) Run TABS once and confirm the log says:
   "TabsAgent Mod loaded."
4) Run Python:
   `python run_llm_file_bridge.py`
5) Confirm files appear in `%LOCALAPPDATA%\TabsAgentModIO\`:
   - `state.json` (written by mod)
   - `plan.json` (written by Python)

## Phase 5: implement real game state reading
1) Open ILSpy and inspect `Assembly-CSharp.dll`.
2) Find the classes that hold:
   - budget
   - current phase (setup/battle)
   - placed units (your + enemy)
3) Implement `GameStateCollector` in:
   `TabsAgentMod/Collectors.cs`

## Phase 6: implement unit placement
1) Find placement/army builder classes in `Assembly-CSharp.dll`.
2) Implement `PlanApplier` in:
   `TabsAgentMod/PlanApplier.cs`
3) Convert normalized `(x, y)` to world coordinates on the RED side.

## Phase 7: data + prompt improvements
1) Update `data/units_dummy.json` with real unit names.
2) Refine prompt rules in `core/strategy_llm.py`.
3) Add tags (melee/ranged/tank/aoe) to improve LLM planning.

## Phase 8: testing
1) Start a sandbox match.
2) Confirm the mod writes state with real units.
3) Confirm the LLM plan appears in `plan.json`.
4) Confirm units are placed correctly in-game.
