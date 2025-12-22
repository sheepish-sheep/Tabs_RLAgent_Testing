using BepInEx;
using System;
using System.Diagnostics;
using System.IO;
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

        private void Awake()
        {
            // Initialize config, file bridge, state collector, and plan applier.
            _config = new ModConfig(Config);
            _bridge = new FileBridge(_config.IoDirectory, Logger);
            _collector = _config.UseMockState
                ? (IGameStateCollector)new MockStateCollector(_config)
                : new GameStateCollector(Logger);
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
            if (Input.GetKeyDown(KeyCode.P))
            {
                Logger.LogInfo("P pressed: writing state and running LLM bridge.");
                WriteStateIfReady();
                bool started = TryRunLlmOnce();
                if (!started)
                {
                    ReadAndApplyPlan();
                }
            }

            // Periodically write state.json and poll plan.json on a timer.
            if (Time.time >= _nextStateWriteTime)
            {
                WriteStateIfReady();
                _nextStateWriteTime = Time.time + _config.StateWriteIntervalSeconds;
            }

            if (Time.time >= _nextPlanPollTime)
            {
                if (_llmProcess == null || _llmProcess.HasExited)
                {
                    ReadAndApplyPlan();
                }
                _nextPlanPollTime = Time.time + _config.PlanPollIntervalSeconds;
            }

            if (_pendingPlanApply)
            {
                _pendingPlanApply = false;
                ReadAndApplyPlan();
            }
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
            _applier.Apply(plan);
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
            string apiKey = _config.OpenAiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = TryReadApiKeyFromEnvFile(_config.BridgeScriptPath);
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.EnvironmentVariables["OPENAI_API_KEY"] = apiKey;
            }
            else
            {
                LogApiKeyWarningOnce("OpenAI API key not set. Add OpenAiApiKey in config or create a .env file next to run_llm_file_bridge.py with OPENAI_API_KEY=...");
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
                    if (!string.Equals(key, "OPENAI_API_KEY", StringComparison.Ordinal))
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
