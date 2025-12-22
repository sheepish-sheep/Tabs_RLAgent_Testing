using System;
using System.IO;
using BepInEx.Configuration;

namespace TabsAgentMod
{
    public class ModConfig
    {
        public string IoDirectory { get; }
        public float StateWriteIntervalSeconds { get; }
        public float PlanPollIntervalSeconds { get; }
        public bool UseMockState { get; }
        public int MockBudget { get; }
        public string PythonExePath { get; }
        public string BridgeScriptPath { get; }
        public string OpenAiApiKey { get; }

        public ModConfig(ConfigFile config)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string defaultIoDirectory = Path.Combine(baseDir, "TabsAgentModIO");
            //Set sensible defaults if config values are missing.
            if (config == null)
            {
                IoDirectory = defaultIoDirectory;
                StateWriteIntervalSeconds = 1.0f;
                PlanPollIntervalSeconds = 1.0f;
                UseMockState = false;
                MockBudget = 1000;
                PythonExePath = "python";
                BridgeScriptPath = string.Empty;
                OpenAiApiKey = string.Empty;
                return;
            }

            // TODO: Bind config values (IO directory, poll intervals, mock mode, etc).
            IoDirectory = config.Bind("General", "IoDirectory", defaultIoDirectory, "Directory for IO files").Value;
            StateWriteIntervalSeconds = config.Bind("General", "StateWriteIntervalSeconds", 1.0f, "Interval in seconds to write state files").Value;
            PlanPollIntervalSeconds = config.Bind("General", "PlanPollIntervalSeconds", 1.0f, "Interval in seconds to poll for plans").Value;
            UseMockState = config.Bind("General", "UseMockState", false, "Whether to use mock state data").Value;
            MockBudget = config.Bind("General", "MockBudget", 1000, "Budget for mock state data").Value;
            PythonExePath = config.Bind("LLM", "PythonExePath", "python", "Path to Python executable").Value;
            BridgeScriptPath = config.Bind("LLM", "BridgeScriptPath", string.Empty, "Path to run_llm_file_bridge.py").Value;
            OpenAiApiKey = config.Bind("LLM", "OpenAiApiKey", string.Empty, "OpenAI API key for LLM bridge").Value;

        }
    }
}
