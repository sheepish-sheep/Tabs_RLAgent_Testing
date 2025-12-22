# Campaign Mode TODO

Purpose
- Campaign mode: always place for Red team and budget is usually lower than enemy total cost.

Stub files created
- `TabsAgentMod/CampaignStateCollector.cs`
- `TabsAgentMod/CampaignPlanApplier.cs`
- `TabsAgentMod/CampaignBudgetPolicy.cs`

TODO
1) ModConfig
   - Add settings: Mode (normal/campaign), CampaignBudgetRatio, CampaignBudgetOffset.
   - Bind defaults and document in config.
2) Models
   - Extend `StatePayload` with Mode, Team, EnemyTotalCost, BudgetRatio (if needed).
   - Keep JSON field names consistent for Python.
3) Plugin wiring
   - Choose collector/applier based on Mode.
   - Use campaign collector/applier when Mode == campaign.
4) CampaignStateCollector
   - Read enemy units from layouts (setup) or TeamSystem (battle).
   - Sum enemy total cost from UnitBlueprint.GetUnitCost().
   - Set `state.Budget = CampaignBudgetPolicy.ComputeBudget(enemyTotalCost, config)`.
   - Force Red team output fields.
5) CampaignBudgetPolicy
   - Compute budget using ratio and offset.
   - Clamp to valid range if needed.
6) Python bridge + LLM rules
   - Use campaign rules when `state.Mode == "campaign"`.
   - Keep Red-only placement and target spend below enemy total cost.
7) Test
   - Verify `state.json` includes enemy total cost (if added).
   - Verify `plan.json` is created and units place on Red side.
