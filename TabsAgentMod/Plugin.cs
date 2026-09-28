using BepInEx;
using Landfall.TABS;
using Landfall.TABS.GameMode;
using Landfall.TABS.GameState;
using Landfall.TABS.Services;
using Landfall.TABS.UnitPlacement;
using Landfall.TABS.Workshop;
using System;
using System.Diagnostics;
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
        private IGameStateCollector _collector;
        private IPlanApplier _applier;
        private float _nextStateWriteTime;
        private float _nextPlanPollTime;
        private bool _loggedMissingBridge;
        private bool _loggedMissingApiKey;
        private Process _llmProcess;
        private bool _pendingPlanApply;
        private string _lastAppliedPlanStateId;
        private bool _hasPendingAnchor;
        private Vector3 _pendingAnchorPos;
        private Quaternion _pendingAnchorRotation;
        private string _pendingAnchorSource;
        private bool _hasEpisodeAnchor;
        private Vector3 _episodeAnchorPos;
        private Quaternion _episodeAnchorRotation;
        private string _episodeAnchorSource;

        private void Awake()
        {
            // Initialize config, file bridge, state collector, and plan applier.
            _config = new ModConfig(Config);
            _bridge = new FileBridge(_config.IoDirectory, Logger);
            _collector = _config.UseMockState
                ? (IGameStateCollector)new MockStateCollector(_config)
                : new GameStateCollector(Logger, _config);
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

            if (Input.GetKeyDown(KeyCode.P))
            {
                Logger.LogInfo("P pressed: writing state and running LLM bridge.");
                WriteStateIfReady();
                if (!PlanApplier.TryResolveAnchor(out _pendingAnchorPos, out _pendingAnchorRotation, out _pendingAnchorSource))
                {
                    Logger.LogError("No valid anchor: point the cursor at RED territory or place a RED unit before running the LLM.");
                    return;
                }
                _hasPendingAnchor = true;
                bool started = TryRunLlmOnce();
                if (!started)
                {
                    ReadAndApplyPlan();
                }
            }

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
                if (_bridge.TryConsumeDumpCostsRawRequest())
                {
                    Logger.LogInfo("Dump-costs-raw request received: writing undeduped blueprint costs.");
                    _applier.DumpAllBlueprintCostsRaw(Path.Combine(_config.IoDirectory, "blueprint_costs_raw.json"));
                }
                if (_llmProcess == null || _llmProcess.HasExited)
                {
                    ReadAndApplyPlan();
                }
                _nextPlanPollTime = Time.unscaledTime + _config.PlanPollIntervalSeconds;
            }

            if (_pendingPlanApply)
            {
                _pendingPlanApply = false;
                ReadAndApplyPlan();
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
            if (_hasPendingAnchor)
            {
                _hasPendingAnchor = false;
                _applier.Apply(plan, _pendingAnchorPos, _pendingAnchorRotation, _pendingAnchorSource, _config.DeploymentBoxHalfExtentX, _config.DeploymentBoxHalfExtentZ);
            }
            else
            {
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
            }
            if (!string.IsNullOrWhiteSpace(plan.StateId))
            {
                _lastAppliedPlanStateId = plan.StateId;
            }
        }

        private bool TryRunLlmOnce()
        {
            if (string.IsNullOrWhiteSpace(_config.BridgeScriptPath))
            {
                LogBridgeWarningOnce("LLM bridge path not set. Update the config with BridgeScriptPath.");
                return false;
            }

            if (!File.Exists(_config.BridgeScriptPath))
            {
                LogBridgeWarningOnce($"LLM bridge script not found: {_config.BridgeScriptPath}");
                return false;
            }
            if (_llmProcess != null && !_llmProcess.HasExited)
            {
                Logger.LogInfo("LLM bridge is already running.");
                return false;
            }

            string pythonExe = string.IsNullOrWhiteSpace(_config.PythonExePath)
                ? "python"
                : _config.PythonExePath;

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{_config.BridgeScriptPath}\" --once --io-dir \"{_config.IoDirectory}\"",
                WorkingDirectory = Path.GetDirectoryName(_config.BridgeScriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.EnvironmentVariables["TABS_AGENT_IO_DIR"] = _config.IoDirectory;
            string apiKey = _config.AnthropicApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = TryReadApiKeyFromEnvFile(_config.BridgeScriptPath);
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = apiKey;
            }
            else
            {
                LogApiKeyWarningOnce("Anthropic API key not set. Add AnthropicApiKey in config or create a .env file next to run_llm_file_bridge.py with ANTHROPIC_API_KEY=...");
            }

            try
            {
                Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        Logger.LogInfo($"LLM bridge: {args.Data}");
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        Logger.LogWarning($"LLM bridge stderr: {args.Data}");
                    }
                };
                process.Exited += (sender, args) =>
                {
                    try
                    {
                        Logger.LogInfo($"LLM bridge exited with code {process.ExitCode}.");
                    }
                    catch
                    {
                        // Ignore process exit race conditions.
                    }
                    _pendingPlanApply = true;
                    _llmProcess = null;
                    process.Dispose();
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _llmProcess = process;
                Logger.LogInfo("LLM bridge started.");
                return true;
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to start LLM bridge: {ex.Message}");
                _llmProcess = null;
                return false;
            }
        }

        private void LogBridgeWarningOnce(string message)
        {
            if (_loggedMissingBridge)
            {
                return;
            }
            Logger.LogWarning(message);
            _loggedMissingBridge = true;
        }

        private void LogApiKeyWarningOnce(string message)
        {
            if (_loggedMissingApiKey)
            {
                return;
            }
            Logger.LogWarning(message);
            _loggedMissingApiKey = true;
        }

        private static string TryReadApiKeyFromEnvFile(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                return null;
            }
            string directory = Path.GetDirectoryName(scriptPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }
            string envPath = Path.Combine(directory, ".env");
            if (!File.Exists(envPath))
            {
                return null;
            }
            try
            {
                string[] lines = File.ReadAllLines(envPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, idx).Trim();
                    if (!string.Equals(key, "ANTHROPIC_API_KEY", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string value = line.Substring(idx + 1).Trim().Trim('"');
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }
    }
}
