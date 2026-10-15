using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Landfall.TABS;
using Landfall.TABS.GameMode;
using Landfall.TABS.GameState;
using Landfall.TABS.UnitPlacement;
using Landfall.TABS.Workshop;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TabsAgentMod
{
    public interface IPlanApplier
    {
        void Apply(PlanPayload plan, Vector3 anchorPos, Quaternion anchorRotation, string anchorSource, float deploymentHalfExtentX = 40f, float deploymentHalfExtentZ = 40f);
        void DumpBlueprintCosts(string outputPath);
    }

    public class PlanApplier : IPlanApplier
    {
        private readonly ManualLogSource _logger;
        private static ManualLogSource _staticLogger;
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
            _staticLogger = logger;
        }

        public void DumpBlueprintCosts(string outputPath)
        {
            // force: true so this reflects the corrected content-database lookup, not
            // whatever was cached before that fix.
            GetBlueprintCaches(out Dictionary<string, UnitBlueprint> blueprintByName,
                out Dictionary<string, UnitBlueprint> _,
                out List<KeyValuePair<string, UnitBlueprint>> __,
                force: true);

            HashSet<UnitBlueprint> seen = new HashSet<UnitBlueprint>();
            List<UnitBlueprint> unique = new List<UnitBlueprint>();
            foreach (UnitBlueprint blueprint in blueprintByName.Values)
            {
                if (blueprint != null && seen.Add(blueprint))
                {
                    unique.Add(blueprint);
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < unique.Count; i++)
            {
                UnitBlueprint blueprint = unique[i];
                string displayName = EscapeJsonString(blueprint.Name);
                string rawName = EscapeJsonString(blueprint.name);
                float cost = blueprint.GetUnitCost();
                sb.Append("  {\"Name\": \"").Append(displayName)
                  .Append("\", \"RawName\": \"").Append(rawName)
                  .Append("\", \"Cost\": ").Append(cost.ToString(CultureInfo.InvariantCulture))
                  .Append("}");
                if (i < unique.Count - 1)
                {
                    sb.Append(",");
                }
                sb.Append("\n");
            }
            sb.Append("]\n");

            File.WriteAllText(outputPath, sb.ToString());
            _logger.LogInfo($"Dumped {unique.Count} distinct blueprint costs to {outputPath}");
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Apply(PlanPayload plan, Vector3 anchorPos, Quaternion anchorRotation, string anchorSource, float deploymentHalfExtentX = 40f, float deploymentHalfExtentZ = 40f)
        {
            BaseGameMode gameMode = ServiceLocator.GetService<GameModeService>()?.CurrentGameMode;
            GameStateManager gameStateManager = gameMode?.GameStateManager;
            if (gameStateManager == null || gameStateManager.GameState != GameState.PlacementState)
            {
                _logger.LogError("Current game mode is not in PlacementState.");
                return;
            }
            // Brush/cursor provide placement APIs and masks.
            UnitPlacementBrush brush = gameMode.Brush;
            if (brush == null)
            {
                _logger.LogError("UnitPlacementBrush not available.");
                return;
            }
            PlacementCursor cursor = brush.Cursor;
            float anchorY = anchorPos.y;
            float localHalfX = deploymentHalfExtentX;
            float localHalfZ = deploymentHalfExtentZ;
            const float localHalfY = 25f;
            Bounds redBounds = default(Bounds);
            redBounds.SetMinMax(
                new Vector3(anchorPos.x - localHalfX, anchorPos.y - localHalfY, anchorPos.z - localHalfZ),
                new Vector3(anchorPos.x + localHalfX, anchorPos.y + localHalfY, anchorPos.z + localHalfZ));

            Vector3 rightDir = anchorRotation * Vector3.right;
            Vector3 forwardDir = anchorRotation * Vector3.forward;
            float minRight = float.MaxValue, maxRight = float.MinValue;
            float minForward = float.MaxValue, maxForward = float.MinValue;
            for (int cx = 0; cx < 2; cx++)
            {
                float cornerX = cx == 0 ? redBounds.min.x : redBounds.max.x;
                for (int cz = 0; cz < 2; cz++)
                {
                    float cornerZ = cz == 0 ? redBounds.min.z : redBounds.max.z;
                    Vector3 offset = new Vector3(cornerX - anchorPos.x, 0f, cornerZ - anchorPos.z);
                    float r = Vector3.Dot(offset, rightDir);
                    float f = Vector3.Dot(offset, forwardDir);
                    if (r < minRight) minRight = r;
                    if (r > maxRight) maxRight = r;
                    if (f < minForward) minForward = f;
                    if (f > maxForward) maxForward = f;
                }
            }

            _logger.LogInfo($"redBounds center={redBounds.center} size={redBounds.size} min={redBounds.min} max={redBounds.max}");
            _logger.LogInfo($"anchor source={anchorSource} pos={anchorPos} forward={forwardDir} right={rightDir}");

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
                                    _logger.LogError(
                                        $"UnitBlueprint not found for placement: {placementName} "
                                        + $"(cache has {blueprintByName.Count} exact-name entries after refresh)");
                                    continue;
                                }
                            }
                        }
                    }

                    bool useWorldCoords = placement.X < 0f || placement.X > 1f || placement.Y < 0f || placement.Y > 1f;
                    float worldX, worldZ;
                    if (useWorldCoords)
                    {
                        worldX = placement.X;
                        worldZ = placement.Y;
                    }
                    else
                    {
                        float localRight = Mathf.Lerp(minRight, maxRight, placement.X);
                        float localForward = Mathf.Lerp(minForward, maxForward, placement.Y);
                        Vector3 target = anchorPos + rightDir * localRight + forwardDir * localForward;
                        worldX = target.x;
                        worldZ = target.z;
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
                        else
                        {
                            worldPos = anchorPos;
                            hasWorldPos = true;
                            _logger.LogWarning($"No ground hit for placement: {placementName}. Using anchor position.");
                        }
                    }
                    
                    if (!useWorldCoords)
                    {
                        float clampedX = Mathf.Clamp(worldPos.x, redBounds.min.x, redBounds.max.x);
                        float clampedZ = Mathf.Clamp(worldPos.z, redBounds.min.z, redBounds.max.z);
                        if (clampedX != worldPos.x || clampedZ != worldPos.z)
                        {
                            worldPos.x = clampedX;
                            worldPos.z = clampedZ;
                        }
                    }

                    if (TryGetGroundY(worldPos.x, worldPos.z, redBounds, out float anchorGroundY))
                    {
                        worldPos.y = anchorGroundY;
                    }
                    else
                    {
                        worldPos.y = anchorY;
                    }

                    Quaternion rotation;
                    // Validate the placement is on RED territory and get the facing.
                    SceneSettings.TeamRequestReslut teamResult = SceneSettings.GetTeamTerritory(worldPos, out rotation);
                    if (!teamResult.CanPlace || teamResult.Team != Team.Red)
                    {
                        _logger.LogError($"Invalid RED placement for unit: {placement.Name}");
                        continue;
                    }
                    rotation = anchorRotation;

                    _logger.LogInfo(
                        $"Placing {placementName} (resolved -> {blueprint.Name}, cost={blueprint.GetUnitCost()}) "
                        + $"at {worldPos} (normalized x={placement.X:F2} y={placement.Y:F2})");

                    // Place the unit using the game's internal placement flow.
                    brush.PlaceUnitInternal(blueprint, Team.Red, worldPos, rotation, true, null);
                    placedCount++;
                }
            }

        }

        internal static bool TryResolveAnchor(out Vector3 anchorPos, out Quaternion anchorRotation, out string anchorSource, bool ignoreCursor = false, float autoBackOffset = 0f)
        {
            BaseGameMode gameMode = ServiceLocator.GetService<GameModeService>()?.CurrentGameMode;
            UnitPlacementBrush brush = gameMode?.Brush;
            if (!ignoreCursor && brush != null && brush.RaycastMap(out Vector3 cursorPos))
            {
                SceneSettings.TeamRequestReslut cursorTeamResult = SceneSettings.GetTeamTerritory(cursorPos, out Quaternion cursorRotation);
                if (cursorTeamResult.CanPlace && cursorTeamResult.Team == Team.Red)
                {
                    anchorPos = cursorPos;
                    anchorRotation = cursorRotation;
                    anchorSource = "cursor";
                    return true;
                }
            }

            Unit anchorUnit = FindRedAnchorUnit();
            if (anchorUnit != null)
            {
                anchorRotation = anchorUnit.transform.rotation;
                anchorPos = ApplyBackOffset(anchorUnit.transform.position, anchorRotation, autoBackOffset);
                anchorSource = $"unit:{anchorUnit.name}";
                return true;
            }

            if (TryResolveTeamLineAnchor(out Vector3 teamLineAnchorPos, out Quaternion teamLineAnchorRotation))
            {
                anchorRotation = teamLineAnchorRotation;
                anchorPos = ApplyBackOffset(teamLineAnchorPos, anchorRotation, autoBackOffset);
                anchorSource = "teamline";
                return true;
            }
            if (TryResolveGridAnchor(out Vector3 gridAnchorPos, out Quaternion gridAnchorRotation))
            {
                anchorRotation = gridAnchorRotation;
                anchorPos = ApplyBackOffset(gridAnchorPos, anchorRotation, autoBackOffset);
                anchorSource = "grid";
                return true;
            }

            anchorPos = default(Vector3);
            anchorRotation = Quaternion.identity;
            anchorSource = null;
            return false;
        }

        // Keeps the original point if the shifted one is off the ground or outside RED territory.
        private static Vector3 ApplyBackOffset(Vector3 pos, Quaternion rotation, float distance)
        {
            if (distance == 0f)
            {
                return pos;
            }

            Vector3 forward = rotation * Vector3.forward;
            float x = pos.x - forward.x * distance;
            float z = pos.z - forward.z * distance;
            Bounds probeBounds = new Bounds(new Vector3(x, pos.y, z), new Vector3(1f, 200f, 1f));
            if (!TryGetGroundY(x, z, probeBounds, out float groundY))
            {
                _staticLogger.LogInfo($"[BackOffset] shifted point ({x:F1},{z:F1}) has no ground; keeping original anchor.");
                return pos;
            }

            Vector3 shifted = new Vector3(x, groundY, z);
            SceneSettings.TeamRequestReslut result = SceneSettings.GetTeamTerritory(shifted, out _);
            if (!result.CanPlace || result.Team != Team.Red)
            {
                _staticLogger.LogInfo($"[BackOffset] shifted point {shifted} is not valid RED territory; keeping original anchor.");
                return pos;
            }

            _staticLogger.LogInfo($"[BackOffset] moved anchor from {pos} to {shifted} (distance={distance}).");
            return shifted;
        }

        private static bool TryResolveGridAnchor(out Vector3 anchorPos, out Quaternion anchorRotation)
        {
            const float halfExtent = 80f;
            const int steps = 11;

            // Check candidates nearest the map centre first; raster order picked a map corner.
            List<(float x, float z)> candidates = new List<(float x, float z)>();
            for (int ix = 0; ix < steps; ix++)
            {
                float x = Mathf.Lerp(-halfExtent, halfExtent, ix / (float)(steps - 1));
                for (int iz = 0; iz < steps; iz++)
                {
                    float z = Mathf.Lerp(-halfExtent, halfExtent, iz / (float)(steps - 1));
                    candidates.Add((x, z));
                }
            }
            candidates.Sort((a, b) => (a.x * a.x + a.z * a.z).CompareTo(b.x * b.x + b.z * b.z));

            foreach ((float x, float z) in candidates)
            {
                Bounds probeBounds = new Bounds(new Vector3(x, 0f, z), new Vector3(1f, 200f, 1f));
                if (!TryGetGroundY(x, z, probeBounds, out float groundY))
                {
                    continue;
                }

                Vector3 candidate = new Vector3(x, groundY, z);
                SceneSettings.TeamRequestReslut result = SceneSettings.GetTeamTerritory(candidate, out Quaternion candidateRotation);
                if (result.CanPlace && result.Team == Team.Red)
                {
                    float distFromOrigin = Mathf.Sqrt(x * x + z * z);
                    _staticLogger.LogInfo($"[GridAnchor] found RED at {candidate} (distance from origin={distFromOrigin:F1})");
                    anchorPos = candidate;
                    anchorRotation = candidateRotation;
                    return true;
                }
            }

            _staticLogger.LogInfo($"[GridAnchor] no RED point found in {steps}x{steps} grid over +/-{halfExtent}.");
            anchorPos = default(Vector3);
            anchorRotation = Quaternion.identity;
            return false;
        }
        private static bool TryResolveTeamLineAnchor(out Vector3 anchorPos, out Quaternion anchorRotation)
        {
            TABSSceneSettings serialized = SceneSettings.SerializedSettings;
            if (serialized == null)
            {
                _staticLogger.LogInfo("[TeamLineAnchor] SceneSettings.SerializedSettings is null.");
                anchorPos = default(Vector3);
                anchorRotation = Quaternion.identity;
                return false;
            }

            Vector3 center = new Vector3(serialized.TeamLineCenterX, serialized.TeamLineCenterY, serialized.TeamLineCenterZ);
            Quaternion lineRotation = Quaternion.Euler(0f, serialized.TeamLineRotation, 0f);
            const float probeDistance = 15f;
            _staticLogger.LogInfo($"[TeamLineAnchor] center={center} rotation={serialized.TeamLineRotation} type={serialized.TeamLineType} radius={serialized.TeamLineRadius} swap={serialized.TeamSwap}");

            for (int i = 0; i < 2; i++)
            {
                float sign = i == 0 ? 1f : -1f;
                Vector3 offset = lineRotation * Vector3.forward * (probeDistance * sign);
                float x = center.x + offset.x;
                float z = center.z + offset.z;

                Bounds probeBounds = new Bounds(new Vector3(x, center.y, z), new Vector3(1f, 100f, 1f));
                if (!TryGetGroundY(x, z, probeBounds, out float groundY))
                {
                    _staticLogger.LogInfo($"[TeamLineAnchor] probe {i} at x={x} z={z}: no ground hit.");
                    continue;
                }

                Vector3 candidate = new Vector3(x, groundY, z);
                SceneSettings.TeamRequestReslut result = SceneSettings.GetTeamTerritory(candidate, out Quaternion candidateRotation);
                _staticLogger.LogInfo($"[TeamLineAnchor] probe {i} candidate={candidate} CanPlace={result.CanPlace} Team={result.Team}");
                if (result.CanPlace && result.Team == Team.Red)
                {
                    anchorPos = candidate;
                    anchorRotation = candidateRotation;
                    return true;
                }
            }

            anchorPos = default(Vector3);
            anchorRotation = Quaternion.identity;
            return false;
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

            List<UnitBlueprint> databaseBlueprints = GetBlueprintsFromRealContentDatabase();
            for (int i = 0; i < databaseBlueprints.Count; i++)
            {
                yield return databaseBlueprints[i];
            }

            _staticLogger.LogInfo(
                $"EnumerateBlueprints: {resources.Length} from Resources.FindObjectsOfTypeAll, "
                + $"{databaseBlueprints.Count} from DM.ContentDatabase.");
        }

        private static Type _contentDatabaseType;
        private static bool _contentDatabaseTypeSearched;

        private static List<UnitBlueprint> GetBlueprintsFromRealContentDatabase()
        {
            List<UnitBlueprint> results = new List<UnitBlueprint>();
            try
            {
                if (!_contentDatabaseTypeSearched)
                {
                    _contentDatabaseTypeSearched = true;
                    _contentDatabaseType = FindTypeByFullName("DM.ContentDatabase");
                    _staticLogger.LogInfo(_contentDatabaseType != null
                        ? $"Resolved DM.ContentDatabase -> {_contentDatabaseType.AssemblyQualifiedName}"
                        : "DM.ContentDatabase type not found in any loaded assembly.");
                }

                if (_contentDatabaseType == null)
                {
                    return results;
                }

                MethodInfo instanceMethod = _contentDatabaseType.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instanceMethod?.Invoke(null, null);
                if (instance == null)
                {
                    _staticLogger.LogInfo("DM.ContentDatabase.Instance() returned null.");
                    return results;
                }

                MethodInfo getAllMethod = _contentDatabaseType.GetMethod("GetAllUnitBlueprints", BindingFlags.Public | BindingFlags.Instance);
                object blueprintsObj = getAllMethod?.Invoke(instance, null);
                IEnumerable blueprintsEnumerable = blueprintsObj as IEnumerable;
                if (blueprintsEnumerable == null)
                {
                    _staticLogger.LogInfo("DM.ContentDatabase.GetAllUnitBlueprints() returned null or non-enumerable.");
                    return results;
                }

                foreach (object item in blueprintsEnumerable)
                {
                    if (item is UnitBlueprint blueprint)
                    {
                        results.Add(blueprint);
                    }
                }
            }
            catch (Exception ex)
            {
                _staticLogger.LogInfo($"GetBlueprintsFromRealContentDatabase failed: {ex}");
            }
            return results;
        }

        private static Type FindTypeByFullName(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type type = assemblies[a].GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
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
