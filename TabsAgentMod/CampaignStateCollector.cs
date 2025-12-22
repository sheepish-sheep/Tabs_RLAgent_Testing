using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace TabsAgentMod
{
    public sealed class CampaignStateCollector : IGameStateCollector
    {
        private readonly ManualLogSource _logger;
        private readonly ModConfig _config;

        public CampaignStateCollector(ManualLogSource logger, ModConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public bool TryCollect(out StatePayload state)
        {
            state = new StatePayload
            {
                StateId = DateTime.UtcNow.Ticks.ToString(),
                Budget = 0,
                Phase = "unknown",
                EnemyUnits = new List<UnitCount>(),
                YourUnits = new List<UnitCount>(),
            };

            return false;
        }
    }
}
