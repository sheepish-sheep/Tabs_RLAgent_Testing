# TabsAgent Mod Crash Course (C# + file-based bridge)

This file tells you what to install, how to build the C# mod in Visual Studio,
and how the JSON bridge works with your Python LangChain planner.
All C# files in `TabsAgentMod/` are TODO stubs; you will implement them.

## Downloads
- BepInEx 5 (x64): https://github.com/BepInEx/BepInEx/releases
- Visual Studio 2022: https://visualstudio.microsoft.com/downloads/
- .NET Framework 4.7.2 Developer Pack: https://dotnet.microsoft.com/download/dotnet-framework/net472
- ILSpy (assembly browser): https://github.com/icsharpcode/ILSpy/releases
- UnityExplorer (optional, in-game inspector): https://github.com/sinai-dev/UnityExplorer/releases

Note: if you downloaded the UnityExplorer repo zip, it is source code only.
For in-game use, download the release plugin zip (BepInEx Mono build).

## C# mini crash course (only what you need)
- Class: a container for data + functions.
  - `public class Foo { public int X; public void Do() { } }`
- Field: data stored on the class (like a variable).
- Method: a function inside a class.
- List and Dictionary:
  - `List<int> nums = new List<int>();`
  - `Dictionary<string, int> map = new Dictionary<string, int>();`
- if / for / foreach:
  - `if (x > 0) { }`
  - `for (int i = 0; i < 10; i++) { }`
  - `foreach (var item in list) { }`
- return: gives a value back from a method.
- using: imports a namespace (like Python import).

## What you are building
- A C# mod for TABS (runs inside the game).
- The mod writes `state.json` to disk.
- Your Python script reads it, uses the LLM, and writes `plan.json`.
- The mod polls `plan.json` and applies placements.

## File-based JSON protocol
State file (written by C#):
```
{
  "state_id": "123",
  "budget": 1000,
  "phase": "setup",
  "enemy_units": [
    { "name": "Clubber", "count": 3 },
    { "name": "Archer", "count": 2 }
  ],
  "your_units": []
}
```

Plan file (written by Python):
```
{
  "state_id": "123",
  "placements": [
    { "name": "Clubber", "x": 0.25, "y": 0.60 },
    { "name": "Archer", "x": 0.40, "y": 0.55 }
  ]
}
```

Coordinates are normalized (0.0 to 1.0) in the RED battlefield half.

Default IO path (both sides):
`%LOCALAPPDATA%\TabsAgentModIO\state.json`
`%LOCALAPPDATA%\TabsAgentModIO\plan.json`

You can change the directory in the BepInEx config or by setting:
`TABS_AGENT_IO_DIR` for the Python script.

## Build steps in Visual Studio
1. Install BepInEx into:
   `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator`
2. Run TABS once, then exit. This creates `BepInEx/`.
3. Open `TabsAgentMod/TabsAgentMod.csproj` in Visual Studio.
4. In `TabsAgentMod.csproj`, confirm `TABS_DIR` matches your install path.
5. Build the project (Build -> Build Solution) after you implement the stubs.
6. Copy the output DLL to:
   `...\Totally Accurate Battle Simulator\BepInEx\plugins\`
7. Run TABS and check:
   `BepInEx\LogOutput.log` for "TabsAgent Mod loaded."

## How to test the bridge (mock data)
1. Implement `Models.cs`, `ModConfig.cs`, and `FileBridge.cs`.
2. Implement `MockStateCollector` so it writes dummy data.
3. In `BepInEx/config/com.tabsagent.mod.cfg`, set:
   - `UseMockState = true`
4. Run TABS.
5. In a terminal, run:
   `python run_llm_file_bridge.py`
6. You should see `state.json` and `plan.json` appear in:
   `%LOCALAPPDATA%\TabsAgentModIO\`

## Where to implement the real game hooks
Open these files in `TabsAgentMod/`:
- `Collectors.cs`: replace `GameStateCollector` with real reads from TABS.
- `PlanApplier.cs`: replace the logger with actual unit placement logic.

Use ILSpy to inspect:
`TotallyAccurateBattleSimulator_Data/Managed/Assembly-CSharp.dll`
Look for classes related to:
- battle setup
- unit placement
- budgets

If you want, tell me the class names you find and I can help wire them up.
