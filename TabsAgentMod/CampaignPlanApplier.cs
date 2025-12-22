using BepInEx.Logging;

namespace TabsAgentMod
{
    public sealed class CampaignPlanApplier : IPlanApplier
    {
        private readonly PlanApplier _planApplier;

        public CampaignPlanApplier(ManualLogSource logger)
        {
            _planApplier = new PlanApplier(logger);
        }

        public void Apply(PlanPayload plan)
        {
            _planApplier.Apply(plan);
        }
    }
}
