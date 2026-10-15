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
                IgnoreCursorForAutoAnchor = true;
                AutoAnchorBackOffset = 15f;
                DeploymentBoxHalfExtentX = 40f;
                DeploymentBoxHalfExtentZ = 40f;
                return;
            }

            IoDirectory = config.Bind("General", "IoDirectory", defaultIoDirectory, "Directory for IO files").Value;
            StateWriteIntervalSeconds = config.Bind("General", "StateWriteIntervalSeconds", 0.1f, "Interval in seconds to write state files").Value;
            PlanPollIntervalSeconds = config.Bind("General", "PlanPollIntervalSeconds", 0.1f, "Interval in seconds to poll for plans").Value;
            IgnoreCursorForAutoAnchor = config.Bind("General", "IgnoreCursorForAutoAnchor", true,
                "Skip the mouse cursor when picking the placement anchor. Leave on for training, "
                + "or an idle cursor can shift where units land between episodes.").Value;
            AutoAnchorBackOffset = config.Bind("General", "AutoAnchorBackOffset", 15f,
                "World units to pull the anchor back from the team boundary (not applied to a cursor anchor). 0 disables it.").Value;
            DeploymentBoxHalfExtentX = config.Bind("General", "DeploymentBoxHalfExtentX", 40f,
                "Half the deployment box size along WORLD x. Shrink it on small levels so units don't land off the map.").Value;
            DeploymentBoxHalfExtentZ = config.Bind("General", "DeploymentBoxHalfExtentZ", 40f,
                "Half the deployment box size along WORLD z.").Value;
        }
    }
}
