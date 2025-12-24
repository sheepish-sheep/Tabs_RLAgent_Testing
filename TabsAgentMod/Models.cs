using System.Collections.Generic;

namespace TabsAgentMod
{
    public class UnitCount
    {
        public string Name;
        public int Count;

    }

    public class StatePayload
    {
        public  string StateId;
        public int Budget;
        public  string Phase;
        public List<UnitCount> EnemyUnits;
        public List<UnitCount> YourUnits;
        public bool Campaign;
        public int CampaignBattleBudget;
    }

    public class Placement
    {
        public string Name;
        public float X;
        public float Y;
    }

    public class PlanPayload
    {
        public string StateId;
        public List<Placement> Placements;
    }
}
