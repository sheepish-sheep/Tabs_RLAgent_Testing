using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Landfall.TABS;
using Landfall.TABS.GameMode;
using Landfall.TABS.GameState;
using Landfall.TABS.Services;
using Landfall.TABS.AI.Systems;
using Landfall.TABS.UnitPlacement;
using Unity.Entities;
using UnityEngine;

namespace TabsAgentMod
{
    public interface IGameStateCollector
    {
        bool TryCollect(out StatePayload state);
        void ResetOutcomeLatch();
    }

    public class GameStateCollector : IGameStateCollector
    {
        private readonly ManualLogSource _logger;
        private readonly ModConfig _config;
        private bool _loggedMissing;


        private bool _hasBeenInBattle;
        private bool _latchedMatchOver;
        private string _latchedWinner;
        private int _latchedWinningTeamUnitsRemaining;
        private int _latchedYourUnitsRemaining;
        private int _latchedEnemyUnitsRemaining;
        private List<UnitCount> _latchedSurvivingEnemyUnits;
        private int _latchedEnemyUnitsRemainingCost;

        public void ResetOutcomeLatch()
        {
            _hasBeenInBattle = false;
            _latchedMatchOver = false;
            _latchedWinner = null;
            _latchedWinningTeamUnitsRemaining = 0;
            _latchedYourUnitsRemaining = 0;
            _latchedEnemyUnitsRemaining = 0;
            _latchedSurvivingEnemyUnits = null;
            _latchedEnemyUnitsRemainingCost = 0;
        }

        public GameStateCollector(ManualLogSource logger, ModConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public bool TryCollect(out StatePayload state)
        {
            state = new StatePayload
            {
                StateId = DateTime.UtcNow.Ticks.ToString(),
                EnemyUnits = new List<UnitCount>(),
                YourUnits = new List<UnitCount>(),
                Phase = "unknown",
                Campaign = false,
                CampaignBattleBudget = 0
            };

            GameModeService service = ServiceLocator.GetService<GameModeService>();
            BaseGameMode mode = service != null ? service.CurrentGameMode : null;
            if (mode == null)
            {
                LogOnce("GameStateCollector: GameMode not ready yet.");
                return false;
            }
            bool isCampaign = mode is CampaignGameMode;
            state.Campaign = isCampaign;

            if (mode.GameStateManager != null)
            {
                GameState phase = mode.GameStateManager.GameState;
                state.Phase = phase == GameState.PlacementState
                    ? "setup"
                    : phase == GameState.BattleState
                        ? "battle"
                        : "unknown";
            }

            List<UnitCount> layoutYour = null;
            List<UnitCount> layoutEnemy = null;
            if (mode.TeamLayouts != null)
            {
                layoutYour = CountLayoutUnits(mode.TeamLayouts, Team.Red);
                layoutEnemy = CollectEnemyUnitsWithPosition(mode.TeamLayouts, GetEnemyTeam());
                state.EnemyStartingCost = SumLayoutBlueprintCost(mode.TeamLayouts, GetEnemyTeam());
            }

            if (state.Phase == "setup")
            {
                state.YourUnits = layoutYour ?? new List<UnitCount>();
                state.EnemyUnits = layoutEnemy ?? new List<UnitCount>();
            }
            else if (state.Phase == "unknown")
            {
                if ((layoutYour != null && layoutYour.Count > 0) || (layoutEnemy != null && layoutEnemy.Count > 0))
                {
                    state.Phase = "setup";
                    state.YourUnits = layoutYour ?? new List<UnitCount>();
                    state.EnemyUnits = layoutEnemy ?? new List<UnitCount>();
                }
                else
                {
                    LogOnce("GameStateCollector: unknown phase, skipping unit counts.");
                }
            }

            int enemyTotalCost = 0;
            int yourTotalCost = 0;
            if (!isCampaign && mode.TeamLayouts != null)
            {
                var enemyLayout = mode.TeamLayouts.GetTeamLayout(GetEnemyTeam());
                if (enemyLayout != null)
                {
                    for (int i = 0; i < enemyLayout.Count; i++)
                    {
                        UnitBlueprint unitBlueprint = GetBlueprintFromLayoutUnit(enemyLayout[i]) as UnitBlueprint;
                        if (unitBlueprint != null)
                        {
                            enemyTotalCost += (int)unitBlueprint.GetUnitCost();
                        }
                    }
                }

                var yourLayout = mode.TeamLayouts.GetTeamLayout(Team.Red);
                if (yourLayout != null)
                {
                    for (int i = 0; i < yourLayout.Count; i++)
                    {
                        UnitBlueprint unitBlueprint = GetBlueprintFromLayoutUnit(yourLayout[i]) as UnitBlueprint;
                        if (unitBlueprint != null)
                        {
                            yourTotalCost += (int)unitBlueprint.GetUnitCost();
                        }
                    }
                }
            }

            if (isCampaign || enemyTotalCost <= 0)
            {
                if (mode.BattleBudget != null)
                {
                    state.Budget = mode.BattleBudget.GetBudget(Team.Red);
                    if (state.Budget <= 0)
                    {
                        state.Budget = mode.BattleBudget.GetMaxBudget(Team.Red);
                    }
                }
                state.CampaignBattleBudget = isCampaign ? state.Budget : 0;
            }
            else
            {
                int remainingBudget = enemyTotalCost - yourTotalCost;
                state.Budget = remainingBudget > 0 ? remainingBudget : 0;
            }

            if (state.Phase == "battle")
            {
                _hasBeenInBattle = true;
            }
            if (_hasBeenInBattle && !_latchedMatchOver && mode.MatchOver)
            {
                _latchedMatchOver = true;
                _latchedWinner = mode.CachedCurrentWinner.ToString();
                TeamSystem teamSystem = World.Active.GetExistingManager<TeamSystem>();
                _latchedWinningTeamUnitsRemaining = teamSystem.GetWinConditionTeamUnits(mode.CachedCurrentWinner).Count;
                _latchedYourUnitsRemaining = teamSystem.GetWinConditionTeamUnits(Team.Red).Count;
                List<Unit> survivingEnemies = teamSystem.GetWinConditionTeamUnits(GetEnemyTeam());
                _latchedEnemyUnitsRemaining = survivingEnemies.Count;
                _latchedSurvivingEnemyUnits = CountUnitsByBlueprint(survivingEnemies);
                _latchedEnemyUnitsRemainingCost = SumUnitBlueprintCost(survivingEnemies);
            }
            state.MatchOver = _latchedMatchOver;
            state.Winner = _latchedWinner;
            state.WinningTeamUnitsRemaining = _latchedWinningTeamUnitsRemaining;
            state.YourUnitsRemaining = _latchedYourUnitsRemaining;
            state.EnemyUnitsRemaining = _latchedEnemyUnitsRemaining;
            state.SurvivingEnemyUnits = _latchedSurvivingEnemyUnits ?? new List<UnitCount>();
            state.EnemyUnitsRemainingCost = _latchedEnemyUnitsRemainingCost;

            return true;
        }

        private static int SumLayoutBlueprintCost(TeamLayouts layouts, Team team)
        {
            if (layouts == null)
            {
                return 0;
            }
            var layout = layouts.GetTeamLayout(team);
            if (layout == null || layout.Count == 0)
            {
                return 0;
            }
            int total = 0;
            for (int i = 0; i < layout.Count; i++)
            {
                UnitBlueprint unitBlueprint = GetBlueprintFromLayoutUnit(layout[i]) as UnitBlueprint;
                if (unitBlueprint != null)
                {
                    total += (int)unitBlueprint.GetUnitCost();
                }
            }
            return total;
        }

        private static int SumUnitBlueprintCost(List<Unit> units)
        {
            if (units == null || units.Count == 0)
            {
                return 0;
            }
            int total = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] != null && units[i].unitBlueprint != null)
                {
                    total += (int)units[i].unitBlueprint.GetUnitCost();
                }
            }
            return total;
        }

        private static List<UnitCount> CountLayoutUnits(TeamLayouts layouts, Team team)
        {
            if (layouts == null)
            {
                return new List<UnitCount>();
            }

            var layout = layouts.GetTeamLayout(team);
            if (layout == null || layout.Count == 0)
            {
                return new List<UnitCount>();
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(layout.Count, StringComparer.Ordinal);
            for (int i = 0; i < layout.Count; i++)
            {
                object blueprint = GetBlueprintFromLayoutUnit(layout[i]);
                AddUnitCount(counts, blueprint);
            }

            return ToUnitCounts(counts);
        }

        // Named survivor counts from a live TeamSystem unit list (post-battle), as opposed
        // to CountLayoutUnits which reads the pre-battle placement plan.
        private static List<UnitCount> CountUnitsByBlueprint(List<Unit> units)
        {
            if (units == null || units.Count == 0)
            {
                return new List<UnitCount>();
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(units.Count, StringComparer.Ordinal);
            for (int i = 0; i < units.Count; i++)
            {
                AddUnitCount(counts, units[i] != null ? units[i].unitBlueprint : null);
            }

            return ToUnitCounts(counts);
        }

        private static List<UnitCount> CollectEnemyUnitsWithPosition(TeamLayouts layouts, Team team) {
            if (layouts == null) {
                return new List<UnitCount>();
            }

            var layout = layouts.GetTeamLayout(team);
            if (layout == null || layout.Count == 0) {
                return new List<UnitCount>();
            }

            List<UnitCount> results = new List<UnitCount>(layout.Count);
            for (int i = 0; i < layout.Count; i++) {
                object blueprint = GetBlueprintFromLayoutUnit(layout[i]);
                LayoutUnitAccessor accessor = GetLayoutUnitAccessor(layout[i].GetType());
                float x = 0;
                float y = 0;
                if (TryGetLayoutUnitPosition(layout[i], accessor, out x, out y)) {
                    results.Add(new UnitCount {Name = GetUnitName(blueprint),Count = 1,X = x,Y = y});
                } else {
                    results.Add(new UnitCount {Name = GetUnitName(blueprint), Count = 1});
                }

            }

            return results;
        }



        private static void AddUnitCount(Dictionary<string, int> counts, object blueprint)
        {
            string name = GetUnitName(blueprint);
            if (counts.TryGetValue(name, out int current))
            {
                counts[name] = current + 1;
            }
            else
            {
                counts.Add(name, 1);
            }
        }

        private static string GetUnitName(object blueprint)
        {
            if (blueprint == null)
            {
                return "Unknown";
            }

            UnitBlueprint unitBlueprint = blueprint as UnitBlueprint;
            if (unitBlueprint != null)
            {
                string unitName = unitBlueprint.Name;
                if (!string.IsNullOrEmpty(unitName))
                {
                    return unitName;
                }
            }

            UnityEngine.Object unityObject = blueprint as UnityEngine.Object;
            if (unityObject == null)
            {
                return "Unknown";
            }

            string name = unityObject.name;
            return string.IsNullOrEmpty(name) ? "Unknown" : name;
        }

        private static List<UnitCount> ToUnitCounts(Dictionary<string, int> counts)
        {
            List<UnitCount> results = new List<UnitCount>(counts.Count);
            foreach (KeyValuePair<string, int> pair in counts)
            {
                results.Add(new UnitCount { Name = pair.Key, Count = pair.Value });
            }
            return results;
        }

        // Possible layout field/property names across game versions.
        private static readonly string[] LayoutUnitBlueprintMemberNames =
        {
            "unitBlueprint",
            "UnitBlueprint",
            "blueprint",
            "Blueprint",
            "m_unitBlueprint",
            "m_UnitBlueprint",
            "m_blueprint",
            "m_Blueprint",
        };

        // Possible nested unit field/property names across game versions.
        private static readonly string[] LayoutUnitMemberNames =
        {
            "unit",
            "Unit",
            "m_unit",
            "m_Unit",
        };

        private static readonly string[] LayoutUnitPositionMemberNames =
        {
            "position",
            "Position",
            "m_position",
            "m_Position",
        };

        // Cache reflection lookups per layout type for speed.
        private static readonly object LayoutUnitAccessorLock = new object();
        private static readonly Dictionary<Type, LayoutUnitAccessor> LayoutUnitAccessors =
            new Dictionary<Type, LayoutUnitAccessor>();

        // Resolve a blueprint reference from a layout unit using cached reflection.
        private static object GetBlueprintFromLayoutUnit(object layoutUnit)
        {
            if (layoutUnit == null)
            {
                return null;
            }

            Type type = layoutUnit.GetType();
            LayoutUnitAccessor accessor = GetLayoutUnitAccessor(type);

            object blueprint = GetMemberValue(layoutUnit, accessor.BlueprintField, accessor.BlueprintProperty);
            if (blueprint != null)
            {
                return blueprint;
            }

            object unit = GetMemberValue(layoutUnit, accessor.UnitField, accessor.UnitProperty);
            if (unit == null)
            {
                return null;
            }

            return GetMemberValue(unit, accessor.NestedBlueprintField, accessor.NestedBlueprintProperty);
        }

        private static bool TryGetLayoutUnitPosition(object layoutUnit, LayoutUnitAccessor accessor, out float x, out float y)
        {
            object raw = GetMemberValue(layoutUnit, accessor.PositionField, accessor.PositionProperty);
            if (raw == null)
            {
                object unit = GetMemberValue(layoutUnit, accessor.UnitField, accessor.UnitProperty);
                raw = GetMemberValue(unit, accessor.NestedPositionField, accessor.NestedPositionProperty);
            }

            if (raw is Vector3 vector3) {
                x = vector3.x;
                y = vector3.z;
                return true;
            } else {
                x = 0;
                y = 0;
                return false;
            }
        }

        // Get or create the cached reflection accessor for the layout type.
        private static LayoutUnitAccessor GetLayoutUnitAccessor(Type type)
        {
            lock (LayoutUnitAccessorLock)
            {
                LayoutUnitAccessor accessor;
                if (LayoutUnitAccessors.TryGetValue(type, out accessor))
                {
                    return accessor;
                }

                accessor = CreateLayoutUnitAccessor(type);
                LayoutUnitAccessors.Add(type, accessor);
                return accessor;
            }
        }

        // Discover the blueprint member on the layout type or nested unit type.
        private static LayoutUnitAccessor CreateLayoutUnitAccessor(Type type)
        {
            LayoutUnitAccessor accessor = new LayoutUnitAccessor();
            accessor.PositionField = FindField(type, LayoutUnitPositionMemberNames);
            accessor.PositionProperty = FindProperty(type, LayoutUnitPositionMemberNames);

            accessor.BlueprintField = FindField(type, LayoutUnitBlueprintMemberNames);
            accessor.BlueprintProperty = FindProperty(type, LayoutUnitBlueprintMemberNames);
            if (accessor.BlueprintField != null || accessor.BlueprintProperty != null)
            {
                return accessor;
            }

            accessor.UnitField = FindField(type, LayoutUnitMemberNames);
            accessor.UnitProperty = FindProperty(type, LayoutUnitMemberNames);
            Type unitType = accessor.UnitField != null
                ? accessor.UnitField.FieldType
                : accessor.UnitProperty != null
                    ? accessor.UnitProperty.PropertyType
                    : null;

            if (unitType == null)
            {
                return accessor;
            }

            accessor.NestedBlueprintField = FindField(unitType, LayoutUnitBlueprintMemberNames);
            accessor.NestedBlueprintProperty = FindProperty(unitType, LayoutUnitBlueprintMemberNames);
            if (accessor.PositionField == null && accessor.PositionProperty == null)
            {
                accessor.NestedPositionField = FindField(unitType, LayoutUnitPositionMemberNames);
                accessor.NestedPositionProperty = FindProperty(unitType, LayoutUnitPositionMemberNames);
            }
            return accessor;
        }

        // Find the first matching field by name.
        private static FieldInfo FindField(Type type, string[] names)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                for (int j = 0; j < fields.Length; j++)
                {
                    if (string.Equals(fields[j].Name, name, StringComparison.Ordinal))
                    {
                        return fields[j];
                    }
                }
            }
            return null;
        }

        // Find the first matching property by name.
        private static PropertyInfo FindProperty(Type type, string[] names)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                for (int j = 0; j < properties.Length; j++)
                {
                    if (string.Equals(properties[j].Name, name, StringComparison.Ordinal))
                    {
                        return properties[j];
                    }
                }
            }
            return null;
        }

        // Read a field or property value safely.
        private static object GetMemberValue(object target, FieldInfo field, PropertyInfo property)
        {
            if (target == null)
            {
                return null;
            }

            if (field != null)
            {
                return field.GetValue(target);
            }

            if (property != null)
            {
                return property.GetValue(target, null);
            }

            return null;
        }

        private static Team GetEnemyTeam()
        {
            Team[] teams = (Team[])Enum.GetValues(typeof(Team));
            for (int i = 0; i < teams.Length; i++)
            {
                Team team = teams[i];
                if (team != Team.Red)
                {
                    return team;
                }
            }

            return Team.Red;
        }

        private struct LayoutUnitAccessor
        {
            public FieldInfo BlueprintField;
            public PropertyInfo BlueprintProperty;
            public FieldInfo UnitField;
            public PropertyInfo UnitProperty;
            public FieldInfo NestedBlueprintField;
            public PropertyInfo NestedBlueprintProperty;
            public FieldInfo PositionField;
            public PropertyInfo PositionProperty;
            public FieldInfo NestedPositionField;
            public PropertyInfo NestedPositionProperty;
        }

        // Log only once to avoid spamming.
        private void LogOnce(string message)
        {
            if (_loggedMissing)
            {
                return;
            }
            _logger.LogInfo(message);
            _loggedMissing = true;
        }
    }

    public class MockStateCollector : IGameStateCollector
    {
        private readonly ModConfig _config;
        private int _counter;

        public MockStateCollector(ModConfig config)
        {
            _config = config;
        }

        public bool TryCollect(out StatePayload state)
        {
            _counter++;
            state = new StatePayload
            {
                StateId = _counter.ToString(),
                Budget = _config.MockBudget,
                Phase = "setup",
                Campaign = _config != null && _config.Campaign,
                CampaignBattleBudget = _config != null ? _config.CampaignBattleBudget : 0,
                EnemyUnits = new List<UnitCount>
                {
                    new UnitCount { Name = "Clubber", Count = 3 },
                    new UnitCount { Name = "Archer", Count = 2 }
                },
                YourUnits = new List<UnitCount>(),
            };
            return true;
        }

        public void ResetOutcomeLatch()
        {
            // No latch to reset - MockStateCollector never reports a battle outcome.
        }
    }
}
