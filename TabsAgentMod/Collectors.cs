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
    }

    public class GameStateCollector : IGameStateCollector
    {
        private readonly ManualLogSource _logger;
        private bool _loggedMissing;

        public GameStateCollector(ManualLogSource logger)
        {
            _logger = logger;
        }

        public bool TryCollect(out StatePayload state)
        {
            state = new StatePayload
            {
                StateId = DateTime.UtcNow.Ticks.ToString(),
                EnemyUnits = new List<UnitCount>(),
                YourUnits = new List<UnitCount>(),
                Phase = "unknown",
            };

            GameModeService service = ServiceLocator.GetService<GameModeService>();
            BaseGameMode mode = service != null ? service.CurrentGameMode : null;
            if (mode == null)
            {
                LogOnce("GameStateCollector: GameMode not ready yet.");
                return false;
            }

            if (mode.BattleBudget != null)
            {
                state.Budget = mode.BattleBudget.GetBudget(Team.Red);
                if (state.Budget <= 0)
                {
                    state.Budget = mode.BattleBudget.GetMaxBudget(Team.Red);
                }
            }

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
                layoutEnemy = CountLayoutUnits(mode.TeamLayouts, GetEnemyTeam());
            }

            if (state.Phase == "setup")
            {
                state.YourUnits = layoutYour ?? new List<UnitCount>();
                state.EnemyUnits = layoutEnemy ?? new List<UnitCount>();
            }
            else if (state.Phase == "battle")
            {
                TeamSystem teamSystem = World.Active != null
                    ? World.Active.GetOrCreateManager<TeamSystem>()
                    : null;
                state.YourUnits = CountLiveUnits(teamSystem, Team.Red);
                state.EnemyUnits = CountLiveUnits(teamSystem, GetEnemyTeam());
            }
            else
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

            return true;
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

        private static List<UnitCount> CountLiveUnits(TeamSystem teamSystem, Team team)
        {
            if (teamSystem == null)
            {
                return new List<UnitCount>();
            }

            List<Unit> units = teamSystem.GetTeamUnits(team);
            if (units == null || units.Count == 0)
            {
                return new List<UnitCount>();
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(units.Count, StringComparer.Ordinal);
            for (int i = 0; i < units.Count; i++)
            {
                Unit unit = units[i];
                if (unit == null)
                {
                    continue;
                }
                AddUnitCount(counts, unit.unitBlueprint);
            }

            return ToUnitCounts(counts);
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
                EnemyUnits = new List<UnitCount>
                {
                    new UnitCount { Name = "Clubber", Count = 3 },
                    new UnitCount { Name = "Archer", Count = 2 }
                },
                YourUnits = new List<UnitCount>(),
            };
            return true;
        }
    }
}
