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
        public string AnthropicApiKey { get; }
        public bool Campaign { get; }
        public int CampaignBattleBudget { get; }
        public bool IgnoreCursorForAutoAnchor { get; }
        public float AutoAnchorBackOffset { get; }
        public float DeploymentBoxHalfExtentX { get; }
        public float DeploymentBoxHalfExtentZ { get; }

        public ModConfig(ConfigFile config)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string defaultIoDirectory = Path.Combine(baseDir, "TabsAgentModIO");
            //Set sensible defaults if config values are missing.
            if (config == null)
            {
                IoDirectory = defaultIoDirectory;
                StateWriteIntervalSeconds = 0.1f;
                PlanPollIntervalSeconds = 0.1f;
                UseMockState = false;
                MockBudget = 1000;
                PythonExePath = "python";
                BridgeScriptPath = string.Empty;
                AnthropicApiKey = string.Empty;
                CampaignBattleBudget = 0;
                Campaign = false;
                IgnoreCursorForAutoAnchor = true;
                AutoAnchorBackOffset = 15f;
                DeploymentBoxHalfExtentX = 40f;
                DeploymentBoxHalfExtentZ = 40f;

                return;
            }

            IoDirectory = config.Bind("General", "IoDirectory", defaultIoDirectory, "Directory for IO files").Value;
            StateWriteIntervalSeconds = config.Bind("General", "StateWriteIntervalSeconds", 0.1f, "Interval in seconds to write state files").Value;
            PlanPollIntervalSeconds = config.Bind("General", "PlanPollIntervalSeconds", 0.1f, "Interval in seconds to poll for plans").Value;
            UseMockState = config.Bind("General", "UseMockState", false, "Whether to use mock state data").Value;
            MockBudget = config.Bind("General", "MockBudget", 1000, "Budget for mock state data").Value;
            Campaign = config.Bind("General", "Campaign", false, "Whether to run in campaign mode").Value;
            CampaignBattleBudget = config.Bind("General", "BattleBudget", 0, "Budget for battle mode (ignored in campaign mode)").Value;
            IgnoreCursorForAutoAnchor = config.Bind("General", "IgnoreCursorForAutoAnchor", true,
                "Whether the automatic (non-P-key) placement path skips the cursor as a possible anchor. "
                + "Confirmed by direct testing: the SAME plan placed 12/12 successfully from a cursor-independent "
                + "anchor but only 7/12 from a cursor anchor near a territory edge -- and because this path runs "
                + "on every reset during unattended RL training, an unwatched mouse position would otherwise make "
                + "the deployment frame silently drift between episodes, adding noise on top of whatever the search "
                + "itself is doing. Leave this on for training. Turn off only if you want automatic placements to "
                + "follow the cursor the way manual P-key placement intentionally does.").Value;
            AutoAnchorBackOffset = config.Bind("General", "AutoAnchorBackOffset", 15f,
                "How far (in world units) to pull the automatic anchor backward, away from the "
                + "enemy/team boundary, before placement. Nearest-to-center grid selection can land "
                + "right at the boundary with no buffer; this adds breathing room. Only affects the "
                + "automatic (non-P-key) path. Set to 0 to disable. Tune by feel and reset the level "
                + "to see the new position -- no rebuild needed.").Value;
            DeploymentBoxHalfExtentX = config.Bind("General", "DeploymentBoxHalfExtentX", 40f,
                "Half-width (in world units) of the deployment box, along the anchor's right axis. "
                + "The box was previously a hardcoded 40 in both directions, sized for one level's "
                + "playable area; on a smaller level that same box can extend past solid ground, "
                + "landing units right at a cliff/map edge where they fall off. Shrink this to fit "
                + "the current level. Affects both the automatic and P-key placement paths. Tune by "
                + "feel and reset the level to see the effect -- no rebuild needed.").Value;
            DeploymentBoxHalfExtentZ = config.Bind("General", "DeploymentBoxHalfExtentZ", 40f,
                "Same as DeploymentBoxHalfExtentX, but along the anchor's forward axis.").Value;

            PythonExePath = config.Bind("LLM", "PythonExePath", "python", "Path to Python executable").Value;
            BridgeScriptPath = config.Bind("LLM", "BridgeScriptPath", string.Empty, "Path to run_llm_file_bridge.py").Value;
            AnthropicApiKey = config.Bind("LLM", "AnthropicApiKey", string.Empty, "Anthropic API key for LLM bridge").Value;

        }
    }
}
