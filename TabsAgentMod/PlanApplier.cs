using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Landfall.TABS;
using Landfall.TABS.GameMode;
using Landfall.TABS.GameState;
using Landfall.TABS.Services;
using Landfall.TABS.UnitPlacement;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TabsAgentMod
{
    public interface IPlanApplier
    {
        void Apply(PlanPayload plan);
    }

    public class PlanApplier : IPlanApplier
    {
        private readonly ManualLogSource _logger;
        private static Dictionary<string, UnitBlueprint> _blueprintByNameCache;
        private static Dictionary<string, UnitBlueprint> _blueprintByNormalizedNameCache;
        private static List<KeyValuePair<string, UnitBlueprint>> _normalizedEntriesCache;
        private static bool _blueprintCacheBuilt;
        private static List<Vector3> _cachedRedGrid;
        private static Bounds _cachedRedBounds;
        private static int _cachedSceneHandle = -1;
        private static int _cachedMask = int.MinValue;
        private static float _cachedMaxAngle = float.MinValue;

        public PlanApplier(ManualLogSource logger)
        {
            _logger = logger;
        }

        public void Apply(PlanPayload plan)
        {
            BaseGameMode gameMode = ServiceLocator.GetService<GameModeService>()?.CurrentGameMode;
            GameStateManager gameStateManager = gameMode?.GameStateManager;
            if (gameStateManager == null || gameStateManager.GameState != GameState.PlacementState)
            {
                _logger.LogError("Current game mode is not in PlacementState.");
                return;
            }
            // Resolve RED-side placement bounds for normalized coord mapping.
            // Find a RED-side placement square to map normalized coords into world space.
            UnitPlacementSquare[] squares = Resources.FindObjectsOfTypeAll<UnitPlacementSquare>();
            UnitPlacementSquare redSquare = null;
            Bounds redBounds = default(Bounds);
            for (int i = 0; i < squares.Length; i++)
            {
                UnitPlacementSquare square = squares[i];
                if (square == null)
                {
                    continue;
                }
                if (!square.gameObject.scene.IsValid())
                {
                    continue;
                }

                Bounds squareBounds;
                Collider squareCollider = square.GetComponentInChildren<Collider>(true);
                if (squareCollider != null)
                {
                    squareBounds = squareCollider.bounds;
                }
                else
                {
                    Renderer squareRenderer = square.GetComponentInChildren<Renderer>(true);
                    if (squareRenderer == null)
                    {
                        continue;
                    }
                    squareBounds = squareRenderer.bounds;
                }
                Quaternion rotation;
                // Use scene territory to detect which team owns this square.
                SceneSettings.TeamRequestReslut teamResult = SceneSettings.GetTeamTerritory(squareBounds.center, out rotation);
                if (!teamResult.CanPlace || teamResult.Team != Team.Red)
                {
                    continue;
                }

                redSquare = square;
                redBounds = squareBounds;
                break;
            }

            if (redSquare == null)
            {
                if (!TryGetRedBoundsFromScene(out redBounds))
                {
                    _logger.LogError("No RED placement bounds found.");
                    return;
                }
            }

            // Brush/cursor provide placement APIs and masks.
            UnitPlacementBrush brush = gameMode.Brush;
            if (brush == null)
            {
                _logger.LogError("UnitPlacementBrush not available.");
                return;
            }
            PlacementCursor cursor = brush.Cursor;
            Unit anchorUnit = FindRedAnchorUnit(redBounds);
            bool hasAnchor = anchorUnit != null;
            if (!hasAnchor)
            {
                _logger.LogError("Red anchor unit not found. Place a RED unit to use as an anchor.");
                return;
            }
            float anchorY = hasAnchor ? anchorUnit.transform.position.y : 0f;
            Quaternion anchorRotation = hasAnchor ? anchorUnit.transform.rotation : Quaternion.identity;

            // Build or reuse blueprint name caches for fast lookup.
            GetBlueprintCaches(out Dictionary<string, UnitBlueprint> blueprintByName,
                out Dictionary<string, UnitBlueprint> blueprintByNormalizedName,
                out List<KeyValuePair<string, UnitBlueprint>> normalizedEntries);
            bool refreshedBlueprintCache = false;

            int placedCount = 0;
            if (plan != null && plan.Placements != null)
            {
                for (int i = 0; i < plan.Placements.Count; i++)
                {
                    Placement placement = plan.Placements[i];
                    // Resolve placement name to a blueprint.
                    string placementName = placement.Name != null ? placement.Name.Trim() : null;
                    if (string.IsNullOrEmpty(placementName))
                    {
                        _logger.LogError("Placement name is empty.");
                        continue;
                    }

                    UnitBlueprint blueprint;
                    if (!blueprintByName.TryGetValue(placementName, out blueprint))
                    {
                        string normalizedName = NormalizeUnitName(placementName);
                        if (string.IsNullOrEmpty(normalizedName) || !blueprintByNormalizedName.TryGetValue(normalizedName, out blueprint))
                        {
                            if (normalizedEntries == null || !TryResolveBlueprintByTokens(placementName, normalizedEntries, out blueprint))
                            {
                                if (!refreshedBlueprintCache)
                                {
                                    refreshedBlueprintCache = true;
                                    GetBlueprintCaches(out blueprintByName, out blueprintByNormalizedName, out normalizedEntries, force: true);
                                    if (blueprintByName.TryGetValue(placementName, out blueprint))
                                    {
                                        // Resolved after refresh.
                                    }
                                    else
                                    {
                                        normalizedName = NormalizeUnitName(placementName);
                                        if (!string.IsNullOrEmpty(normalizedName))
                                        {
                                            blueprintByNormalizedName.TryGetValue(normalizedName, out blueprint);
                                        }
                                        if (blueprint == null && normalizedEntries != null)
                                        {
                                            TryResolveBlueprintByTokens(placementName, normalizedEntries, out blueprint);
                                        }
                                    }
                                }

                                if (blueprint == null)
                                {
                                    _logger.LogError($"UnitBlueprint not found for placement: {placementName}");
                                    continue;
                                }
                            }
                        }
                    }

                    // Map normalized coords into world space (or use world coords if out of [0,1]).
                    bool useWorldCoords = placement.X < 0f || placement.X > 1f || placement.Y < 0f || placement.Y > 1f;
                    int offsetIndex = placedCount;
                    float worldX = useWorldCoords
                        ? placement.X
                        : Mathf.Lerp(redBounds.min.x, redBounds.max.x, placement.X);
                    float worldZ = useWorldCoords
                        ? placement.Y
                        : Mathf.Lerp(redBounds.min.z, redBounds.max.z, placement.Y);
                    if (hasAnchor && !useWorldCoords)
                    {
                        Vector3 anchorPos = anchorUnit.transform.position;
                        Vector3 rightDir = anchorRotation * Vector3.right;
                        Vector3 forwardDir = anchorRotation * Vector3.forward;
                        float localSpanX = 3f;
                        float localSpanZ = 5f;
                        float localX = (Mathf.Clamp01(placement.X) - 0.5f) * localSpanX;
                        float localZ = (Mathf.Clamp01(placement.Y) - 0.5f) * localSpanZ;
                        int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(plan.Placements.Count)));
                        int row = offsetIndex / columns;
                        int col = offsetIndex % columns;
                        float spacingX = 3.5f;
                        float spacingZ = 4.5f;
                        float spreadX = (col - (columns - 1) * 0.5f) * spacingX;
                        float spreadZ = row * spacingZ;
                        Vector3 anchored = anchorPos + rightDir * (localX + spreadX) + forwardDir * (localZ + spreadZ);
                        worldX = anchored.x;
                        worldZ = anchored.z;
                    }
                    Vector3 worldPos = default(Vector3);
                    bool hasWorldPos = false;
                    if (!useWorldCoords && cursor != null)
                    {
                        hasWorldPos = TryGetPlacementPoint(worldX, worldZ, redBounds, cursor, out worldPos);
                    }

                    if (!hasWorldPos && !useWorldCoords)
                    {
                        // Fallback: snap to nearest valid RED point in a precomputed grid.
                        List<Vector3> validRedPoints = GetValidRedPlacementGrid(redBounds, cursor);
                        if (validRedPoints != null && validRedPoints.Count > 0)
                        {
                            worldPos = FindNearestPointXZ(new Vector3(worldX, 0f, worldZ), validRedPoints);
                            hasWorldPos = true;
                        }
                    }

                    if (!hasWorldPos)
                    {
                        if (TryGetGroundY(worldX, worldZ, redBounds, out float groundY))
                        {
                            worldPos = new Vector3(worldX, groundY, worldZ);
                            hasWorldPos = true;
                        }
                        else if (hasAnchor)
                        {
                            worldPos = anchorUnit.transform.position;
                            hasWorldPos = true;
                            _logger.LogWarning($"No ground hit for placement: {placementName}. Using anchor position.");
                        }
                    }
                    if (hasAnchor)
                    {
                        if (TryGetGroundY(worldPos.x, worldPos.z, redBounds, out float anchorGroundY))
                        {
                            worldPos.y = anchorGroundY;
                        }
                        else
                        {
                            worldPos.y = anchorY;
                        }
                    }

                    Quaternion rotation;
                    // Validate the placement is on RED territory and get the facing.
                    SceneSettings.TeamRequestReslut teamResult = SceneSettings.GetTeamTerritory(worldPos, out rotation);
                    if (!teamResult.CanPlace || teamResult.Team != Team.Red)
                    {
                        _logger.LogError($"Invalid RED placement for unit: {placement.Name}");
                        continue;
                    }
                    if (hasAnchor)
                    {
                        rotation = anchorRotation;
                    }

                    // Place the unit using the game's internal placement flow.
                    brush.PlaceUnitInternal(blueprint, Team.Red, worldPos, rotation, true, null);
                    placedCount++;
                }
            }

        }

        internal static Unit FindRedAnchorUnit(Bounds? bounds = null)
        {
            Unit[] units = Resources.FindObjectsOfTypeAll<Unit>();
            Unit fallback = null;
            for (int i = 0; i < units.Length; i++)
            {
                Unit unit = units[i];
                if (unit == null)
                {
                    continue;
                }
                if (!unit.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (!unit.gameObject.scene.IsValid())
                {
                    continue;
                }
                if (unit.Team != Team.Red)
                {
                    continue;
                }
                if (bounds.HasValue)
                {
                    Vector3 pos = unit.transform.position;
                    Bounds checkBounds = bounds.Value;
                    if (pos.x < checkBounds.min.x || pos.x > checkBounds.max.x
                        || pos.z < checkBounds.min.z || pos.z > checkBounds.max.z)
                    {
                        if (fallback == null)
                        {
                            fallback = unit;
                        }
                        continue;
                    }
                    if (!TryGetGroundY(pos.x, pos.z, checkBounds, out _))
                    {
                        if (fallback == null)
                        {
                            fallback = unit;
                        }
                        continue;
                    }
                }
                return unit;
            }

            return fallback;
        }

        private static void GetBlueprintCaches(
            out Dictionary<string, UnitBlueprint> blueprintByName,
            out Dictionary<string, UnitBlueprint> blueprintByNormalizedName,
            out List<KeyValuePair<string, UnitBlueprint>> normalizedEntries,
            bool force = false)
        {
            if (!force && _blueprintCacheBuilt && _blueprintByNameCache != null && _blueprintByNormalizedNameCache != null)
            {
                blueprintByName = _blueprintByNameCache;
                blueprintByNormalizedName = _blueprintByNormalizedNameCache;
                normalizedEntries = _normalizedEntriesCache;
                return;
            }

            blueprintByName = new Dictionary<string, UnitBlueprint>(StringComparer.Ordinal);
            blueprintByNormalizedName = new Dictionary<string, UnitBlueprint>(StringComparer.Ordinal);
            HashSet<UnitBlueprint> seenBlueprints = new HashSet<UnitBlueprint>();
            foreach (UnitBlueprint blueprint in EnumerateBlueprints())
            {
                if (blueprint == null)
                {
                    continue;
                }
                if (!seenBlueprints.Add(blueprint))
                {
                    continue;
                }

                AddBlueprintName(blueprintByName, blueprint.name, blueprint);
                AddBlueprintName(blueprintByNormalizedName, NormalizeUnitName(blueprint.name), blueprint);
                string displayName = blueprint.Name;
                if (!string.Equals(displayName, blueprint.name, StringComparison.Ordinal))
                {
                    AddBlueprintName(blueprintByName, displayName, blueprint);
                    AddBlueprintName(blueprintByNormalizedName, NormalizeUnitName(displayName), blueprint);
                }
            }

            normalizedEntries = blueprintByNormalizedName.Count > 0
                ? new List<KeyValuePair<string, UnitBlueprint>>(blueprintByNormalizedName)
                : null;

            _blueprintByNameCache = blueprintByName;
            _blueprintByNormalizedNameCache = blueprintByNormalizedName;
            _normalizedEntriesCache = normalizedEntries;
            _blueprintCacheBuilt = true;

        }

        private static void AddBlueprintName(Dictionary<string, UnitBlueprint> map, string key, UnitBlueprint value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!map.ContainsKey(key))
            {
                map.Add(key, value);
            }
        }

        private static bool TryGetRedBoundsFromScene(out Bounds redBounds)
        {
            redBounds = default(Bounds);
            // Fallback: scan renderers to approximate scene bounds.
            Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            bool hasSceneBounds = false;
            Bounds sceneBounds = default(Bounds);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }
                if (!renderer.gameObject.scene.IsValid())
                {
                    continue;
                }
                if (!hasSceneBounds)
                {
                    sceneBounds = renderer.bounds;
                    hasSceneBounds = true;
                }
                else
                {
                    sceneBounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasSceneBounds)
            {
                return false;
            }

            const int steps = 9;
            bool foundRed = false;
            float minX = 0f;
            float maxX = 0f;
            float minZ = 0f;
            float maxZ = 0f;
            float y = sceneBounds.center.y;
            for (int ix = 0; ix < steps; ix++)
            {
                float tx = ix / (float)(steps - 1);
                float x = Mathf.Lerp(sceneBounds.min.x, sceneBounds.max.x, tx);
                for (int iz = 0; iz < steps; iz++)
                {
                    float tz = iz / (float)(steps - 1);
                    float z = Mathf.Lerp(sceneBounds.min.z, sceneBounds.max.z, tz);
                    Quaternion rotation;
                    SceneSettings.TeamRequestReslut teamResult = SceneSettings.GetTeamTerritory(new Vector3(x, y, z), out rotation);
                    if (!teamResult.CanPlace || teamResult.Team != Team.Red)
                    {
                        continue;
                    }

                    if (!foundRed)
                    {
                        minX = maxX = x;
                        minZ = maxZ = z;
                        foundRed = true;
                    }
                    else
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (z < minZ) minZ = z;
                        if (z > maxZ) maxZ = z;
                    }
                }
            }

            if (!foundRed)
            {
                return false;
            }

            Vector3 min = new Vector3(minX, sceneBounds.min.y, minZ);
            Vector3 max = new Vector3(maxX, sceneBounds.max.y, maxZ);
            redBounds.SetMinMax(min, max);
            return true;
        }

        private static bool TryGetGroundY(float x, float z, Bounds bounds, out float y)
        {
            // Raycast straight down to find ground height.
            float startY = bounds.max.y + 10f;
            float rayLength = Mathf.Max(100f, (startY - bounds.min.y) + 50f);
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(x, startY, z), Vector3.down, out hit, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.UseGlobal))
            {
                y = hit.point.y;
                return true;
            }
            if (Physics.Raycast(new Vector3(x, startY, z), Vector3.down, out hit, rayLength, Physics.AllLayers, QueryTriggerInteraction.Collide))
            {
                y = hit.point.y;
                return true;
            }

            y = 0f;
            return false;
        }

        private static bool TryGetPlacementPoint(float x, float z, Bounds bounds, PlacementCursor cursor, out Vector3 point)
        {
            // Raycast with cursor masks and reject invalid/slope hits.
            float startY = bounds.max.y + 10f;
            float rayLength = Mathf.Max(100f, (startY - bounds.min.y) + 50f);
            int primaryMask = cursor.PrimaryActionMask.value;
            int invalidMask = cursor.InvalidUnitPlacementMask.value;
            int mask = primaryMask | invalidMask;
            if (mask == 0)
            {
                mask = Physics.AllLayers;
            }

            if (TryFindValidHit(
                Physics.RaycastAll(new Vector3(x, startY, z), Vector3.down, rayLength, mask, QueryTriggerInteraction.UseGlobal),
                cursor,
                primaryMask,
                invalidMask,
                requirePrimaryMask: true,
                out point))
            {
                return true;
            }

            return TryFindValidHit(
                Physics.RaycastAll(new Vector3(x, startY, z), Vector3.down, rayLength, Physics.AllLayers, QueryTriggerInteraction.Collide),
                cursor,
                primaryMask,
                invalidMask,
                requirePrimaryMask: false,
                out point);
        }

        private static bool TryFindValidHit(RaycastHit[] hits, PlacementCursor cursor, int primaryMask, int invalidMask, bool requirePrimaryMask, out Vector3 point)
        {
            if (hits == null || hits.Length == 0)
            {
                point = default(Vector3);
                return false;
            }

            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null)
                {
                    continue;
                }

                float angle = Vector3.Angle(hit.normal, Vector3.up);
                if (angle > cursor.MaxPlacementAngleEuler)
                {
                    continue;
                }

                int hitLayerMask = 1 << hit.collider.gameObject.layer;
                if ((invalidMask & hitLayerMask) != 0)
                {
                    continue;
                }
                if (requirePrimaryMask && primaryMask != 0 && (primaryMask & hitLayerMask) == 0)
                {
                    continue;
                }

                point = hit.point;
                return true;
            }

            point = default(Vector3);
            return false;
        }

        private static List<Vector3> BuildValidRedPlacementGrid(Bounds bounds, PlacementCursor cursor)
        {
            // Sample the RED area to produce valid placement points.
            const int steps = 21;
            List<Vector3> points = new List<Vector3>(steps * steps);
            for (int ix = 0; ix < steps; ix++)
            {
                float tx = ix / (float)(steps - 1);
                float x = Mathf.Lerp(bounds.min.x, bounds.max.x, tx);
                for (int iz = 0; iz < steps; iz++)
                {
                    float tz = iz / (float)(steps - 1);
                    float z = Mathf.Lerp(bounds.min.z, bounds.max.z, tz);
                    Vector3 hitPoint = default(Vector3);
                    bool hasHit = false;
                    if (cursor != null)
                    {
                        hasHit = TryGetPlacementPoint(x, z, bounds, cursor, out hitPoint);
                    }
                    else if (TryGetGroundY(x, z, bounds, out float groundY))
                    {
                        hitPoint = new Vector3(x, groundY, z);
                        hasHit = true;
                    }

                    if (!hasHit)
                    {
                        continue;
                    }

                    Quaternion rotation;
                    SceneSettings.TeamRequestReslut teamResult = SceneSettings.GetTeamTerritory(hitPoint, out rotation);
                    if (!teamResult.CanPlace || teamResult.Team != Team.Red)
                    {
                        continue;
                    }

                    points.Add(hitPoint);
                }
            }

            return points;
        }

        private static List<Vector3> GetValidRedPlacementGrid(Bounds bounds, PlacementCursor cursor)
        {
            // Cache the grid by scene/bounds/mask to avoid rebuilding every Apply.
            int sceneHandle = SceneManager.GetActiveScene().handle;
            int mask = cursor != null
                ? cursor.PrimaryActionMask.value | cursor.InvalidUnitPlacementMask.value
                : int.MinValue;
            float maxAngle = cursor != null ? cursor.MaxPlacementAngleEuler : float.MinValue;

            if (_cachedRedGrid != null
                && _cachedSceneHandle == sceneHandle
                && _cachedMask == mask
                && Mathf.Abs(_cachedMaxAngle - maxAngle) <= 0.01f
                && BoundsApproximately(_cachedRedBounds, bounds))
            {
                return _cachedRedGrid;
            }

            _cachedRedGrid = BuildValidRedPlacementGrid(bounds, cursor);
            _cachedRedBounds = bounds;
            _cachedSceneHandle = sceneHandle;
            _cachedMask = mask;
            _cachedMaxAngle = maxAngle;
            return _cachedRedGrid;
        }

        private static bool BoundsApproximately(Bounds left, Bounds right)
        {
            return Approximately(left.center.x, right.center.x)
                && Approximately(left.center.y, right.center.y)
                && Approximately(left.center.z, right.center.z)
                && Approximately(left.size.x, right.size.x)
                && Approximately(left.size.y, right.size.y)
                && Approximately(left.size.z, right.size.z);
        }

        private static bool Approximately(float left, float right)
        {
            return Mathf.Abs(left - right) <= 0.01f;
        }

        private static Vector3 FindNearestPointXZ(Vector3 target, List<Vector3> points)
        {
            Vector3 bestPoint = points[0];
            float bestDist = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 candidate = points[i];
                float dx = candidate.x - target.x;
                float dz = candidate.z - target.z;
                float dist = (dx * dx) + (dz * dz);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPoint = candidate;
                }
            }

            return bestPoint;
        }

        private static IEnumerable<UnitBlueprint> EnumerateBlueprints()
        {
            UnitBlueprint[] resources = Resources.FindObjectsOfTypeAll<UnitBlueprint>();
            for (int i = 0; i < resources.Length; i++)
            {
                yield return resources[i];
            }

            object database = GetContentDatabaseInstance();
            if (database == null)
            {
                yield break;
            }

            List<UnitBlueprint> databaseBlueprints = new List<UnitBlueprint>();
            AddBlueprintsFromDatabase(database, databaseBlueprints);
            for (int i = 0; i < databaseBlueprints.Count; i++)
            {
                yield return databaseBlueprints[i];
            }
        }

        private static void AddBlueprintsFromDatabase(object database, List<UnitBlueprint> results)
        {
            Type type = database.GetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                AddBlueprintsFromCollection(fields[i].GetValue(database), results, new HashSet<object>(), 0);
            }

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < properties.Length; i++)
            {
                if (!properties[i].CanRead)
                {
                    continue;
                }
                AddBlueprintsFromCollection(properties[i].GetValue(database, null), results, new HashSet<object>(), 0);
            }
        }

        private static void AddBlueprintsFromCollection(object collection, List<UnitBlueprint> results, HashSet<object> visited, int depth)
        {
            if (collection == null)
            {
                return;
            }

            if (collection is UnitBlueprint directBlueprint)
            {
                results.Add(directBlueprint);
                return;
            }

            if (depth > 3)
            {
                return;
            }

            if (!visited.Add(collection))
            {
                return;
            }

            IDictionary dictionary = collection as IDictionary;
            if (dictionary != null)
            {
                foreach (object value in dictionary.Values)
                {
                    AddBlueprintsFromCollection(value, results, visited, depth + 1);
                }
                return;
            }

            if (collection is KeyValuePair<object, object> kv)
            {
                AddBlueprintsFromCollection(kv.Value, results, visited, depth + 1);
                return;
            }

            Type itemType = collection.GetType();
            if (itemType.IsGenericType && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                PropertyInfo valueProperty = itemType.GetProperty("Value");
                if (valueProperty != null)
                {
                    AddBlueprintsFromCollection(valueProperty.GetValue(collection, null), results, visited, depth + 1);
                }
                return;
            }

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable == null)
            {
                return;
            }

            foreach (object item in enumerable)
            {
                AddBlueprintsFromCollection(item, results, visited, depth + 1);
            }
        }

        private static object GetContentDatabaseInstance()
        {
            Type databaseType = Type.GetType("ContentDatabase");
            if (databaseType == null)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && databaseType == null; i++)
                {
                    databaseType = assemblies[i].GetType("ContentDatabase");
                }
            }

            if (databaseType == null)
            {
                return null;
            }

            MethodInfo instanceMethod = databaseType.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceMethod != null)
            {
                return instanceMethod.Invoke(null, null);
            }

            PropertyInfo instanceProperty = databaseType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceProperty != null)
            {
                return instanceProperty.GetValue(null, null);
            }

            return null;
        }

        private static bool TryResolveBlueprintByTokens(string name, List<KeyValuePair<string, UnitBlueprint>> normalizedEntries, out UnitBlueprint blueprint)
        {
            blueprint = null;
            string[] tokens = TokenizeName(name);
            if (tokens.Length == 0)
            {
                return false;
            }

            int bestScore = -1;
            for (int i = 0; i < normalizedEntries.Count; i++)
            {
                string candidate = normalizedEntries[i].Key;
                int score = 0;
                bool allMatch = true;
                for (int t = 0; t < tokens.Length; t++)
                {
                    string token = tokens[t];
                    if (candidate.IndexOf(token, StringComparison.Ordinal) < 0)
                    {
                        allMatch = false;
                        break;
                    }
                    score += token.Length;
                }

                if (!allMatch)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    blueprint = normalizedEntries[i].Value;
                }
            }

            return blueprint != null;
        }

        private static string[] TokenizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Array.Empty<string>();
            }

            List<string> tokens = new List<string>();
            int len = name.Length;
            char[] buffer = new char[len];
            int count = 0;
            for (int i = 0; i < len; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c))
                {
                    buffer[count++] = char.ToUpperInvariant(c);
                }
                else if (count > 0)
                {
                    if (count > 1)
                    {
                        tokens.Add(new string(buffer, 0, count));
                    }
                    count = 0;
                }
            }

            if (count > 1)
            {
                tokens.Add(new string(buffer, 0, count));
            }

            return tokens.Count == 0 ? Array.Empty<string>() : tokens.ToArray();
        }

        private static string NormalizeUnitName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            int len = name.Length;
            char[] buffer = new char[len];
            int count = 0;
            for (int i = 0; i < len; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c))
                {
                    buffer[count++] = char.ToUpperInvariant(c);
                }
            }

            return count == 0 ? string.Empty : new string(buffer, 0, count);
        }
    }
}
