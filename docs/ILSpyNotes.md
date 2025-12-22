# ILSpy Notes (TABS core classes)

This file summarizes the classes you found in `Assembly-CSharp.dll` so you can
reference them later without re-searching.

## Budget
- `Landfall.TABS.Budget.BattleBudget`
  - Holds budgets per team.
  - Key methods: `GetBudget(Team team)`, `GetMaxBudget(Team team)`.

## Game mode + phase
- `Landfall.TABS.GameMode.BaseGameMode`
  - Properties:
    - `BattleBudget` (Budget for teams)
    - `TeamLayouts` (placements during setup)
    - `GameStateManager` (current phase)
  - In sandbox, the active mode is `Landfall.TABS.GameMode.SandboxGameMode`,
    which inherits from `BaseGameMode`.

- `Landfall.TABS.GameState.GameStateManager`
  - Property: `GameState` (enum)
  - Look for values like `PlacementState` and `BattleState`.

## Live units (during battle or after placement)
- `Landfall.TABS.AI.Systems.TeamSystem`
  - Methods:
    - `GetTeamUnits(Team team)` -> `List<Unit>`
    - `GetAllUnits()` -> `List<Unit>`
  - Use `World.Active.GetOrCreateManager<TeamSystem>()` to access it.

- `Landfall.TABS.Unit`
  - Key fields:
    - `Team` (red)
    - `unitBlueprint` (UnitBlueprint reference)
  - Use `unitBlueprint` to map to a name or ID.

## Placement layout (units placed in setup)
- `Landfall.TABS.UnitPlacement.TeamLayouts`
  - Use `GetTeamLayout(Team.Red)` to get layout lists.
  - Layout entries are `TABSCampaignLevelAsset.TABSLayoutUnit` in BaseGameMode.

- `Landfall.TABS.UnitLayout.Unit`
  - Holds placement data (position, rotation, unit type).
  - Useful if you need exact placed positions from layout data.

## Unit placement helpers (for PlanApplier)
- `Landfall.TABS.UnitPlacement.UnitPlacementBrush`
- `Landfall.TABS.UnitPlacement.PlacementSquare`
- `Landfall.TABS.UnitPlacement.PlacementUI`

These classes are likely used for spawning or placing units during setup.

## Where to read data in code (collector plan)
- Budget:
  - `BaseGameMode.BattleBudget.GetBudget(Team.Red)`
- Phase:
  - `BaseGameMode.GameStateManager.GameState`
  - Map `PlacementState` -> "setup", `BattleState` -> "battle".
- Units (battle):
  - `TeamSystem.GetTeamUnits(Team.Red)`
- Units (setup):
  - `BaseGameMode.TeamLayouts.GetTeamLayout(Team.Red)`

Note: Field names in Unity classes can differ. If you are unsure, open the
specific type in ILSpy and verify the exact property/field names.
