# TABS API Notes (Observed)

Summary of classes and methods referenced while building the TabsAgent mod. Collected from ILSpy and in-game usage.

## Placement Flow

- `GameModeService.CurrentGameMode.Brush` -> `UnitPlacementBrush` (main entry point for placement).
- `UnitPlacementBrush.PlaceUnitInternal(UnitBlueprint blueprint, Team team, Vector3 position, Quaternion rotation, bool addToLayout, RuntimeReference runtimeReference, bool costsBudget = true, bool campaignUnit = false, bool checkUserPlacementMaxUnits = false, int martianInstanceId = 0)`
  - Checks budget and max-units (depending on mode), then delegates to `m_brushBehaviour.Place(...)`.
- `UnitPlacementBrush.RaycastMap(out Vector3 pos)`
  - Raycasts from `m_sequencer.GetCameraRay()` using `m_cursor.PrimaryActionMask`.
- `BrushBehaviourSandbox.PlaceUnit(...)`
  - Calls `UnitPlacementBrush.PlaceUnitInternal(...)` with `addToLayout: true`.
- `PlacementCursor`
  - `PrimaryActionMask`, `InvalidUnitPlacementMask`, `SecondaryActionMask`, `UnitMask`, `MaxPlacementAngleEuler`.
  - `SetCursorState(PlacementCursorState state)` controls validity/visuals.
  - `Update()` orients the cursor using `SceneSettings.GetTeamTerritory(...)`.
  - `Awake()` wires `m_unitPlacementBrush` from `GameModeService.CurrentGameMode.Brush`.

## Team Territory / Borders

- `SceneSettings.GetTeamTerritory(Vector3 point, out Quaternion rotation) -> TeamRequestReslut`
  - `TeamRequestReslut` has `bool CanPlace` and `Team Team`.
- `GetTeamTerritoryInternal(...)`
  - Uses `EdgeType.Line` or `EdgeType.Circle`.
- `GetTeamTerritoryLine(...)`
  - Returns Red based on which side of the line you are on (swapped if `m_TeamIsSwapped`).
- `GetTeamTerritoryCircle(...)`
  - Returns Red if outside the radius, otherwise the opposing team (swapped if `m_TeamIsSwapped`).

## Placement Zones

- `UnitPlacementSquare : MonoBehaviour`
  - Empty class; use child `Collider` or `Renderer` bounds to find placement bounds.
- `Resources.FindObjectsOfTypeAll<UnitPlacementSquare>()`
  - Used to find placement squares in the scene.

## Layout Data

- `TABSCampaignLevelAsset.TABSLayoutUnit`
  - `UnitBlueprint m_unitBlueprint`
  - `Vector3 m_position`
  - `bool CampaignUnit`
  - `float Rotation`
  - `SerializedRuntimeReference m_serializedRuntimeReference`
  - `ReferenceType<Unit> m_runtimeReference`
  - `Quaternion GetRotation()` -> `Quaternion.Euler(0f, Rotation, 0f)`
  - `DeserializeRuntimeReference()`

## Units and Blueprints

- `UnitBlueprint.Name`
  - Returns `Entity.Name` if present, otherwise `base.name`.
- `Unit`
  - `unitBlueprint` holds the blueprint reference.
  - `Team` indicates the unit's team.
- `PlacementCursor.ChangedUnitBrush(UnitBlueprint blueprint)`
  - Uses `blueprint.PlacementMesh` or `Unit.PlacementSquare.Size` for cursor scale.

## Game State / Mode

- `GameModeService.CurrentGameMode.GameStateManager.GameState`
  - `GameState.PlacementState`, `GameState.BattleState`, etc.
- `GameModeService.CurrentGameMode.Brush`
  - `UnitPlacementBrush` instance used by the placement system.

## Gotchas

- `PlacementSquare` is not the same as `UnitPlacementSquare`; the latter is the usable MonoBehaviour.
- Placement bounds must be derived from `Collider` or `Renderer` on the placement square.
