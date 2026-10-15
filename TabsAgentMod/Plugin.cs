using BepInEx;
using Landfall.TABS;
using Landfall.TABS.GameMode;
using Landfall.TABS.GameState;
using Landfall.TABS.UnitPlacement;
using Landfall.TABS.Workshop;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TabsAgentMod
{
    [BepInPlugin("com.tabsagent.mod", "TabsAgent Mod", "0.1.0")]
    public class TabsAgentPlugin : BaseUnityPlugin
    {
        private ModConfig _config;
        private FileBridge _bridge;
        private GameStateCollector _collector;
        private IPlanApplier _applier;
        private float _nextStateWriteTime;
        private float _nextPlanPollTime;
        private string _lastAppliedPlanStateId;
        private bool _hasEpisodeAnchor;
        private Vector3 _episodeAnchorPos;
        private Quaternion _episodeAnchorRotation;
        private string _episodeAnchorSource;

        private void Awake()
        {
            // Initialize config, file bridge, state collector, and plan applier.
            _config = new ModConfig(Config);
            _bridge = new FileBridge(_config.IoDirectory, Logger);
            _collector = new GameStateCollector(Logger);
            _applier = new PlanApplier(Logger);

            // Log startup information so you can verify the mod loaded.
            string statePath = Path.Combine(_config.IoDirectory, "state.json");
            string planPath = Path.Combine(_config.IoDirectory, "plan.json");
            Logger.LogInfo("TabsAgent Mod loaded." +
                           $"State Path: {statePath}, " +
                           $"Plan Path: {planPath}");
        }

        private void Update()
        {
            AutoLoadNextLevelProperty?.SetValue(null, false);

            // Manual-testing hotkeys; a new plan.json is applied by the poll loop below, no key needed.
            if (Input.GetKeyDown(KeyCode.R))
            {
                Logger.LogInfo("R pressed: resetting level.");
                ResetLevel();
            }

            if (Input.GetKeyDown(KeyCode.B))
            {
                Logger.LogInfo("B pressed: starting battle.");
                StartBattle();
            }

            if (Time.unscaledTime >= _nextStateWriteTime)
            {
                WriteStateIfReady();
                _nextStateWriteTime = Time.unscaledTime + _config.StateWriteIntervalSeconds;
            }

            if (Time.unscaledTime >= _nextPlanPollTime)
            {
                if (_bridge.TryConsumeResetRequest())
                {
                    Logger.LogInfo("Reset request received: resetting level.");
                    ResetLevel();
                }
                if (_bridge.TryConsumeStartBattleRequest())
                {
                    Logger.LogInfo("Start-battle request received: starting battle.");
                    StartBattle();
                }
                if (_bridge.TryConsumeDumpCostsRequest())
                {
                    Logger.LogInfo("Dump-costs request received: writing blueprint costs.");
                    _applier.DumpBlueprintCosts(Path.Combine(_config.IoDirectory, "blueprint_costs.json"));
                }
                ReadAndApplyPlan();
                _nextPlanPollTime = Time.unscaledTime + _config.PlanPollIntervalSeconds;
            }
        }

        private static readonly FieldInfo CampaignModeLastLoadedLevelField =
            typeof(CampaignGameMode).GetField("m_lastLoadedLevel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RestartMatchMethod =
            typeof(BaseGameMode).GetMethod("RestartMatch", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo AutoLoadNextLevelProperty =
            typeof(CampaignPlayerDataHolder).GetProperty("AutoLoadNextLevel", BindingFlags.Public | BindingFlags.Static);

        private void ResetLevel()
        {
            _collector.ResetOutcomeLatch();
            _hasEpisodeAnchor = false;

            BaseGameMode mode = ServiceLocator.GetService<GameModeService>()?.CurrentGameMode;
            CampaignGameMode campaignMode = mode as CampaignGameMode;
            if (campaignMode != null)
            {
                ResetCampaignLevel(campaignMode);
                return;
            }

            if (mode == null || RestartMatchMethod == null)
            {
                Logger.LogError("Cannot reset: no active game mode, or RestartMatch() not found.");
                return;
            }
            RestartMatchMethod.Invoke(mode, null);
        }

        private void ResetCampaignLevel(CampaignGameMode campaignMode)
        {
            if (campaignMode.MatchOver)
            {
                if (campaignMode.IsMenuOpen())
                {
                    return;
                }
                if (RestartMatchMethod == null)
                {
                    return;
                }
                RestartMatchMethod.Invoke(campaignMode, null);
                campaignMode.OnClearedTeam(Team.Red);
                return;
            }

            TABSCampaignLevelAsset level = CampaignHandler.LastLoadedLevel;
            if (CampaignModeLastLoadedLevelField != null)
            {
                TABSCampaignLevelAsset modeLevel = CampaignModeLastLoadedLevelField.GetValue(campaignMode) as TABSCampaignLevelAsset;
                if (modeLevel != null)
                {
                    level = modeLevel;
                }
            }

            if (level == null)
            {
                Logger.LogError("Cannot reset: no level found on CampaignHandler.LastLoadedLevel or CampaignGameMode.m_lastLoadedLevel.");
                return;
            }
            CampaignHandler.LoadLevel(level, campaign: true);
        }

        private void StartBattle()
        {
            BaseGameMode mode = ServiceLocator.GetService<GameModeService>()?.CurrentGameMode;
            if (mode != null && mode.DelayedEndGameRoutine != null)
            {
                mode.OnWinnerDecided(mode.CachedCurrentWinner, string.Empty);
                return;
            }
            GameStateManager gameStateManager = mode?.GameStateManager;
            if (gameStateManager == null || gameStateManager.GameState != GameState.PlacementState)
            {
                Logger.LogError("Cannot start battle: current game mode is not in PlacementState.");
                return;
            }
            mode.StartGame();
        }

        private void WriteStateIfReady()
        {
            // Collect state from the game and write state.json.
            if (_collector.TryCollect(out StatePayload state))
            {
                _bridge.WriteState(state);
            }
        }

        private void ReadAndApplyPlan()
        {
            // Read plan.json and apply placements in-game.
            PlanPayload plan = _bridge.TryReadPlan();
            if (plan == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(plan.StateId) && string.Equals(plan.StateId, _lastAppliedPlanStateId, StringComparison.Ordinal))
            {
                return;
            }
            if (!_hasEpisodeAnchor)
            {
                if (!PlanApplier.TryResolveAnchor(out _episodeAnchorPos, out _episodeAnchorRotation, out _episodeAnchorSource, _config.IgnoreCursorForAutoAnchor, _config.AutoAnchorBackOffset))
                {
                    Logger.LogError("No valid anchor: cannot apply plan.");
                    return;
                }
                _hasEpisodeAnchor = true;
            }
            _applier.Apply(plan, _episodeAnchorPos, _episodeAnchorRotation, _episodeAnchorSource, _config.DeploymentBoxHalfExtentX, _config.DeploymentBoxHalfExtentZ);
            if (!string.IsNullOrWhiteSpace(plan.StateId))
            {
                _lastAppliedPlanStateId = plan.StateId;
            }
        }

    }
}
