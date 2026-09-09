# TABS Campaign Bot — GA/CMA-ES vs PPO

Compare an evolutionary algorithm against PPO on troop placement for TABS
campaign levels, both driven through the existing `TabsAgentMod` JSON
bridge (originally built for LLM-driven play).

## Repo layout

Kept simple rather than extracting a shared bridge repo:

- **`Tabs_Agent`** — the original LLM-driven project (LangChain +
  `strategy_llm.py`). `TabsAgentMod` (the C# mod + `state.json`/`plan.json`
  schema) lives here.
- **`Tabs_RLAgent_Testing`** (this project) — a full fork of `Tabs_Agent`,
  including its own copy of `TabsAgentMod`. GA/CMA-ES and PPO strategies
  replace `strategy_llm.py`; no LLM involved.

Trade-off accepted: the two copies of `TabsAgentMod` (this repo's and
`Tabs_Agent`'s) aren't shared, so bridge fixes — including the Phase 0
work below — need to be manually ported between them if you want both
projects to have them. Revisit extracting a shared bridge repo later if
that gets painful.

## Phase 0 — Extend the bridge (blocking everything else)

Current `state.json`/`plan.json` schema is missing two things needed for
training:

1. **Enemy unit position.** `GameStateCollector.CountLayoutUnits()`
   currently tallies `{Name, Count}` and discards position. The raw
   layout data (`TeamLayouts.GetTeamLayout(team)`) already carries
   per-unit `m_position` — stop throwing it away, add
   `{Name, X, Y}` per enemy unit to `EnemyUnits`.
2. **Battle outcome.** No win/loss signal exists today —
   `TryCollect()` only runs during `Phase == "setup"` and no-ops during
   `BattleState`. Confirmed via decompile (ILSpy against
   `Assembly-CSharp.dll`) that this is simpler than it looks: no event
   wiring needed, just read two public properties already on
   `BaseGameMode` (via `GameModeService.CurrentGameMode`) once
   `Phase == "battle"`:
   - `MatchOver` (`bool`) — true once the battle has resolved
   - `CachedCurrentWinner` (`Team`) — set by `DecideWinner()` /
     `OnWinnerDecided()` when the match ends
   For a margin metric, `LastTeamStandingWinCondition.OnUnitDied()`
   calls `TeamSystem.GetWinConditionTeamUnits(team).Count` to check
   remaining units per team — same call works for us as a simple
   "units remaining on the winning side" margin.
3. **Latency.** Default poll intervals (1s C#-side, 0.5s Python-side)
   are fine for an LLM turn but too slow for thousands of training
   steps. Shrink `PlanPollIntervalSeconds` or bypass the polling loop
   and call `PlanApplier.Apply()` more directly for the training path.
4. **Level reset, for `reset(stage)` in Phase 1.** Also confirmed via
   decompile: `CampaignHandler.LoadLevel(TABSCampaignLevelAsset level,
   bool campaign = false)` is `public static`, and
   `CampaignHandler.LastLoadedLevel` is a public static getter — so
   `CampaignHandler.LoadLevel(CampaignHandler.LastLoadedLevel,
   campaign: true)` reloads the current level with no reflection
   needed. This is the exact call `CampaignGameMode.OnEnterNewScene()`
   uses internally, so it's a real reset path the game itself relies
   on, not a workaround. (`BaseGameMode.RestartMatch()` /
   `EnterPlacementMode()` are the other route the game uses for the
   "restart battle" button, but both are `protected` — would need
   reflection. `CampaignHandler.LoadLevel` is cleaner.)

These are additive schema changes — `strategy_llm.py` keeps working
unmodified once they land.

## Phase 1 — Environment wrapper

Gym-style interface over the bridge, shared by both algorithms:

- `reset(stage)` — clears board, loads a stage, returns initial state
- `step(action)` — writes a placement to `plan.json`, waits for
  `state.json` to reflect the result, returns `(state, reward, done, info)`
- State encoding: enemy unit-type counts, enemy position (now
  available post–Phase 0), remaining budget, stage id/embedding
- Reward: win/loss (+ margin once available) from the new outcome field

Validate with a random policy before building anything smarter — confirm
rewards match what actually happens in the game window.

## Phase 2 — GA / CMA-ES (build first)

- Single fixed level to start
- Individual = full army: `[(unit_type, x, y), ...]` up to budget
- Fitness = battle outcome from the env wrapper (average top candidates
  over 2–3 runs to smooth TABS's physics noise)
- Use an existing library (`pycma` / `cmaes`) rather than a from-scratch
  implementation
- Exit criterion: converges to a consistently winning army on the fixed
  level

## Phase 3 — PPO (build second, same env)

- Same fixed level, same wrapper
- Sequential per-unit placement, discrete head (unit type) + continuous
  head (x, y), value head for advantage estimation
- Sparse terminal reward = battle outcome
- Use Stable-Baselines3 rather than a from-scratch PPO
- Expect it to need meaningfully more simulations than GA for the same
  level — that's expected, not a bug

## Phase 4 — Comparison

- **Version A (fair head-to-head):** same fixed level, plot win rate vs.
  number of real simulations consumed for each method. Primary axis is
  simulation budget, not wall-clock.
- **Version B (generalization, PPO-favoring):** train PPO across several
  levels, test zero-shot/fine-tuned on a held-out level; compare against
  GA run fresh on that held-out level. Frame as amortized cost across
  many levels, not a straight fight.
- Apply the same noise-handling (re-eval averaging) to both methods'
  reported results.

## Constraints to keep in mind throughout

- Single real TABS instance, sequential simulation runs — no
  multi-instance parallelism, no custom rewritten simulator.
- Every simulation is expensive relative to compute — this is why
  simulation-budget (not wall-clock) is the primary comparison metric,
  and why GA/PPO both need noise-averaging on top candidates rather than
  on every individual.
