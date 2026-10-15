# TABS Agent runbook

Everything needed to run, check, tune and archive training without asking for help.
Written 2026-09-20 from what was actually verified during setup. Where something was
**not** run end to end, it says so.

All commands are **Windows PowerShell 5.1** (no `&&`, and `Tee-Object` has no `-Encoding`).

---

## 1. Where things live

| What | Path |
|---|---|
| Project root | `C:\Users\bense\Downloads\Tabs_RLAgent_Testing` |
| Python env | `.venv\` (Python 3.12) |
| Training entry point | [core/ga_search.py](core/ga_search.py) (`python -m core.ga_search`) |
| Battle loop / reward / timeouts | [core/env.py](core/env.py) |
| Unit list (139 units) | [data/units_dummy.json](data/units_dummy.json) |
| Live checkpoint | `checkpoints/ga_latest.pkl` |
| Best army so far | `checkpoints/best_army_plan.json` (+ `best_army_meta.json`) |
| Training log | `training.log` (project root) |
| Archived levels | `checkpoints/<level_name>/` |
| Mod source (C#) | [TabsAgentMod/](TabsAgentMod/) |
| **Mod settings file** | `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator\BepInEx\config\com.tabsagent.mod.cfg` |
| Mod log | `C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator\BepInEx\LogOutput.log` (rewritten each TABS launch) |
| Mod plugin DLL | `...\Totally Accurate Battle Simulator\BepInEx\plugins\TabsAgentMod.dll` |
| Python <-> game files | `%LOCALAPPDATA%\TabsAgentModIO\` |

The IO folder files:

| File | Written by | Meaning |
|---|---|---|
| `state.json` | mod | live game state (phase, budget, enemy units, outcome) |
| `plan.json` | Python | army to place |
| `reset.json` | Python | reset the level |
| `start_battle.json` | Python | start the battle |
| `dump_costs.json` | you/Python | ask the mod to dump unit blueprint costs to `blueprint_costs.json` |

In-game hotkeys: **R** reset level, **B** start battle. Placing a saved army (e.g. copying
`best_army_plan.json` to `plan.json`) needs no key -- the mod's poll loop applies any new
`plan.json` automatically.

---

## 2. Starting a session

1. Launch TABS, load the campaign level, and get to the **placement/setup** phase.
2. Start training:

```powershell
cd C:\Users\bense\Downloads\Tabs_RLAgent_Testing
.\.venv\Scripts\python.exe -u -m core.ga_search 2>&1 | Tee-Object -FilePath training.log -Append
```

- If `checkpoints/ga_latest.pkl` exists and its encoding version matches, it **resumes**.
  If not, it starts fresh at generation 0 (and says so).
- To force a fresh start, move or delete `ga_latest.pkl` (see section 4).
- Do **not** run two things at once that drive the game (training and the consistency test
  below) - they share `plan.json` / `reset.json`.
- Training **stops by itself** when a generation's best re-evaluated cost reaches `-1.0`
  or better and prints `Converged: found a consistently winning army.` That is a normal exit.

Stop it manually:

```powershell
Get-Process python -ErrorAction SilentlyContinue | Stop-Process -Force
```

---

## 3. Health check (one pass)

```powershell
# 1. Are both running?
Get-Process python, TotallyAccurateBattleSimulator -ErrorAction SilentlyContinue | Select-Object ProcessName, Id, StartTime

# 2. Is the game responding? (age should be ~0 seconds)
$s = Get-Content "$env:LOCALAPPDATA\TabsAgentModIO\state.json" | ConvertFrom-Json
"Phase=$($s.Phase) Budget=$($s.Budget) MatchOver=$($s.MatchOver) EnemyCost=$($s.EnemyStartingCost)"
"state.json age: {0:N1}s" -f ((Get-Date) - (Get-Item "$env:LOCALAPPDATA\TabsAgentModIO\state.json").LastWriteTime).TotalSeconds

# 3. Progress (reads the checkpoint - reliable even if the log is messy)
.\.venv\Scripts\python.exe -c "import pickle; c=pickle.load(open('checkpoints/ga_latest.pkl','rb')); print('generation:', c['generations']); print('best cost ever:', c['best_ever_cost']); print('sigma:', round(c['es'].sigma,4))"

# 4. Training log
Select-String -Path training.log -Pattern "Generation|New best army|Converged" | Select-Object -Last 10
"timeouts: " + @(Select-String -Path training.log -Pattern "timed out").Count

# 5. Placement health from the mod log
$log = "C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator\BepInEx\LogOutput.log"
"placed:              " + @(Select-String $log -Pattern "Placing ").Count
"Invalid RED placement: " + @(Select-String $log -Pattern "Invalid RED placement").Count
"blueprint not found:  " + @(Select-String $log -Pattern "UnitBlueprint not found").Count
"exceptions:           " + @(Select-String $log -Pattern "exception").Count
```

What to expect:

| Signal | Healthy | Worry if |
|---|---|---|
| python + TABS | both running | one died (log just ends, usually no error) |
| `state.json` age | ~0 s | growing - the mod stopped writing |
| checkpoint | updated within about one generation | not updating |
| `Invalid RED placement` | ~0 | more than a few percent of `placed` -> retune placement (section 5) |
| `blueprint not found` | 0 (the 10 bad units are filtered) | any - see section 9 |
| exceptions | 0 | any |
| timeouts | some | more than ~15% of battles (a generation is ~67 battles) |
| `[TabsEnv] battle did not start ... resending start request` lines | occasional | many per generation - the game keeps ignoring start requests |
| a generation scoring exactly `0.00` | never | its 3 re-evaluations all timed out |

Reading the CMA row printed each generation
(`Iterat #Fevals function value axis ratio sigma min&max std t[m:s]`):
- **function value** - best single battle of the generation. Lucky, not reliable.
- **sigma** - search width. Falling slowly = converging. Rising = no consistent direction.
- **axis ratio** - above 1 means it has learned structure between genes.
- **t[m:s]** - cumulative minutes for the whole run (persists across resumes).

A generation is 64 battles (one per candidate) plus 3 re-evaluation battles of the best.
Observed pace: roughly 25-65 minutes per generation, mostly depending on timeouts.

---

## 4. Finishing a level and starting the next

Stop training, then archive (move, don't copy, so the default path is empty):

```powershell
Get-Process python -ErrorAction SilentlyContinue | Stop-Process -Force
cd C:\Users\bense\Downloads\Tabs_RLAgent_Testing
New-Item -ItemType Directory -Force -Path "checkpoints\<level_name>"
Move-Item checkpoints\ga_latest.pkl        "checkpoints\<level_name>\"
Move-Item checkpoints\best_army_plan.json  "checkpoints\<level_name>\"
Move-Item checkpoints\best_army_meta.json  "checkpoints\<level_name>\"
Move-Item training.log                    "checkpoints\<level_name>\"
```

Then for the next level:
1. Load the new level in TABS, reach setup phase.
2. **Start placement settings from the defaults (15 / 40 / 40)**, not the last level's values.
3. Do the placement test and tune (section 5).
4. Start training.

Rules:
- Nothing records the **level name** - only the enemy starting cost is visible. Name folders yourself.
- `checkpoints/` is in `.gitignore`. **Deleting is permanent.** Archive by moving.
- A checkpoint is **not resumable** after you change placement settings or the unit list
  (the genes then mean something different). Treat any such change as a fresh start.

---

## 5. Placement settings (what they do and how to tune them)

Edit the settings file (path in section 1), under `[General]`. **A full TABS close and
relaunch is required** for a change to apply - the mod reads the file once at launch, and a
level reset does not reload it. Editing while TABS is closed is fine.

| Setting | Default | What it does |
|---|---|---|
| `IgnoreCursorForAutoAnchor` | `true` | Ignores the mouse when choosing the anchor. Leave `true` for training; `false` anchors on the cursor. |
| `AutoAnchorBackOffset` | `15` | Slides the deployment box backward (away from the enemy) by this many world units. `0` disables it. If the shifted point is invalid the mod silently keeps the original anchor (log: `keeping original anchor`). |
| `DeploymentBoxHalfExtentX` | `40` | Half the box size along **world x**. Total size is 2x this. |
| `DeploymentBoxHalfExtentZ` | `40` | Half the box size along **world z**. |

**X and Z are world axes.** To know which one is depth, read the last
`anchor source=... forward=(a, b, c)` line in the mod log:
- `forward=(±1,0,0)` -> **X is depth** (front-to-back), Z is width.
- `forward=(0,0,±1)` -> the reverse.

How a unit's position is decided:
- The saved army stores each unit's X/Y as **fractions (0-1) of the box**, not map coordinates.
- The **anchor** (box centre) is picked in this order: cursor (unless ignored) -> an
  already-placed RED unit -> the level's team line -> a grid scan (11x11 over +-80, nearest
  to world origin first). Then the back offset is applied (never to a cursor anchor).
- If a requested spot has no valid RED ground, the mod moves the unit to the **nearest valid
  point** (often a rim or ledge - units can fall off). A spot past the team line becomes
  `Invalid RED placement` and the unit is dropped.

Symptoms:

| You see | Meaning | Try |
|---|---|---|
| `Invalid RED placement` errors | box pokes past the team line / out of RED territory | raise the offset, or shrink the depth |
| Units piled on the map edge / falling off; many units placed away from the requested spot | box hangs off the playable ground | shrink that axis; use the offset to centre the box on the playable area |

Useful log lines: `redBounds min=... max=...` (the box in world coordinates),
`anchor source=... pos=...` (its centre), `[BackOffset] moved anchor from ... to ...`.

**Tuning loop (per level):**
1. Edit the settings file. Fully close TABS and relaunch. Load the level.
2. Reset the level and place a test army (section 8).
3. Check the log:
   ```powershell
   Select-String -Path "C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator\BepInEx\LogOutput.log" -Pattern "Invalid RED placement|BackOffset|redBounds|anchor source" | Select-Object -Last 12
   ```
4. Repeat until clean.

A normal test army has only 4-6 units and barely exercises the edges. A plan with ~25 cheap
units spread across the whole box would show exactly which spots fail or get moved. That
"stress plan" is **not built yet**.

**Recorded automatically:** every new `best_army_meta.json` gets a `placement_config` block
with the four values above (read from the settings file at save time). It reflects the
*file*, not necessarily what the running game loaded - if you edit the file mid-run without
relaunching TABS, the recorded values will be wrong.

Other settings in the file (rarely touched): `IoDirectory`, `StateWriteIntervalSeconds` (0.1),
`PlanPollIntervalSeconds` (0.1). Older entries (`UseMockState`, `MockBudget`, `Campaign`,
`BattleBudget`, the `[LLM]` section) are no longer read by the mod; they're harmless and can
be deleted from the file.

Example, for reference only: on the level with enemy cost 4,610 (forward = +x) the playable
RED ground measured roughly x -40..0 with the team line at 0, giving `4 / 17 / 30`. Those
numbers were estimated from the log; runs on them converged, but they were not separately
confirmed with the placement test.

---

## 6. Scoring and when training stops

`cost = -reward`. Lower is better.

```
reward = enemy_destroyed                       (fraction of enemy COST killed, 0..1)
       + 0.25 * own_survival                   (only added on a win)
```

| Outcome | cost |
|---|---|
| Timeout, or a loss that killed nothing | `0.0` (worst) |
| Loss after killing 45% of the enemy | `-0.45` |
| Win, none of your units survived | `-1.0` |
| Flawless win | `-1.25` (best) |

- A **win means `-1.0` or better.** A win is the only way to reach exactly 100% destroyed.
- `best_ever_cost` and the "best army" are judged on the **average of 3 fresh battles**
  of the generation's best candidate, not on the single lucky battle. So a `-1.25` seen in a
  population row can turn into `-0.73` once re-tested - that is by design.
- Training exits when that 3-battle average is `<= -1.0`. A single win plus two near-total
  losses cannot average below about `-1.08`, so a result of `-1.10` or lower means at least
  two of the three were real wins.
- **Timeouts** (battle over 180 s, reset over 60 s) are scored a flat `0.0` regardless of how
  the battle was going. A winning army that runs out of clock is punished the same as a total
  failure. Not fixed (see section 9).

---

## 7. Settings in the code

`core/ga_search.py`:

| Constant | Value | Meaning |
|---|---|---|
| `MAX_UNITS` | 25 | army slots per candidate (fewer are used - units that don't fit the budget are skipped) |
| `GENES_PER_UNIT` | 3 | `[unit type, x, y]` per slot -> 75 numbers per candidate |
| `POPSIZE` | 64 | candidates per generation (bigger works better than re-testing each one) |
| `POPULATION_REEVALUATIONS` | 1 | battles per candidate in a generation |
| `NUM_REEVALUATIONS` | 3 | battles used to judge each generation's best |
| `SIGMA0` | 1.0 | starting search width |
| `POSITION_SPAN` | 8.0 | position genes run -4..+4 and map linearly onto the box |
| `ENCODING_VERSION` | 4 | bump whenever what the genes mean changes; old checkpoints then start fresh |
| `UNRESOLVABLE_UNITS` | 10 names | units removed from the search (section 9) |
| `TABS_MOD_CONFIG_PATH` (env var) | optional | override where the settings file is read from |

`core/env.py`: `BATTLE_TIMEOUT_SECONDS` 180, `RESET_TIMEOUT_SECONDS` 60,
`DEFAULT_TIMEOUT_SECONDS` 180, `OWN_SURVIVAL_WEIGHT` 0.25.

---

## 8. Replaying and testing an army

Pre-conditions for a faithful replay: the **same level** loaded, and the **same placement
settings** it was found under (see `placement_config` in its `best_army_meta.json`).

**Place an army in position** (resets first). Change the plan path on the first line.
The plan-rewrite step was tested on a scratch copy; the full command was not run as one
line against the live game.

```powershell
$plan = "C:\Users\bense\Downloads\Tabs_RLAgent_Testing\checkpoints\<level_name>\best_army_plan.json"
$io = "$env:LOCALAPPDATA\TabsAgentModIO"
'{"RequestId":"x"}' | Set-Content "$io\reset.json"; Start-Sleep 8
$p = Get-Content $plan -Raw | ConvertFrom-Json; $p.StateId = [guid]::NewGuid().ToString()
$p | ConvertTo-Json -Depth 5 | Set-Content "$io\plan.json" -Encoding utf8
```

Then press **B** in-game, or:

```powershell
'{"RequestId":"b"}' | Set-Content "$env:LOCALAPPDATA\TabsAgentModIO\start_battle.json"
```

- The mod ignores a plan whose `StateId` it already applied, so it must be fresh each time.
- If nothing appears, run the block again - a plan that arrives before the reset finishes is
  silently discarded.
- Skip the first two lines if you are already in setup with nothing placed.

**Consistency test** - replays one army N times through the same reset/place/battle cycle
training uses and counts wins. The script was syntax-checked and matched against `env.py`;
it has **not** been run against the live game. Stop training first.

```powershell
cd C:\Users\bense\Downloads\Tabs_RLAgent_Testing
@'
import json, os, sys
from pathlib import Path
from core.env import TabsEnv

plan_path = sys.argv[1]
runs = int(sys.argv[2])
plan = json.load(open(plan_path, encoding="utf-8-sig"))
placements = [(p["Name"], p["X"], p["Y"]) for p in plan["Placements"]]
env = TabsEnv(Path(os.environ["LOCALAPPDATA"]) / "TabsAgentModIO")

costs, wins = [], 0
for i in range(1, runs + 1):
    try:
        env.reset()
        state, reward, done, info = env.place_army(placements)
        if not done:
            state, reward, done, info = env.start_battle()
        won = info.get("winner") == "Red"
        wins += won
        costs.append(-reward)
        print(f"run {i:2d}: {'WIN ' if won else 'loss'}  cost {-reward:+.3f}  enemy destroyed {info.get('enemy_destroyed', 0):.0%}", flush=True)
    except TimeoutError:
        costs.append(0.0)
        print(f"run {i:2d}: TIMEOUT (scored 0.0)", flush=True)

print(f"\nwins {wins}/{runs}   mean cost {sum(costs) / len(costs):+.3f}   (<= -1.0 means reliably winning)")
'@ | .\.venv\Scripts\python.exe - "checkpoints\<level_name>\best_army_plan.json" 10
```

Read `wins N/10` first. Around 8/10 or better is consistent; near 5/10 means the saved score
was partly luck (it only had to hold up over 3 battles). Each run is roughly 30-60 s, more
if a battle times out.

---

## 9. Known issues and gotchas

**The 10 units that never place.** These exist in the game files but are not in the unit
database this install loads (it has 186 blueprints, none failed to load - the 10 simply are
not in the list):
Celestial Aegis, Chronomancer, Death Bringer, Divine Arbiter, Exiled Sentinel,
Righteous Paladin, Shadow Walker, Tempest Lich, Void Cultist, Void Monarch.
They are filtered out of both CMA-ES and PPO (`UNRESOLVABLE_UNITS` in `core/unit_catalog.py`),
so new runs cannot pick them. Armies found **before** the filter may contain them - on replay those units
silently do not appear. Nothing further can be fixed from the mod side.

**Timeouts add noise.** About 15-20% of battles timed out in one observed run (75 over
7 generations), and one generation's re-evaluation timed out three times in a row, so a real
win scored `0.00`.

A likely cause: the game silently ignores a start-battle request unless both teams already
have units placed, and the mod handles each request file only once. The plan and the start
request are written ~0.09 s apart, close to the mod's 0.1 s poll, so about 1 in 10 starts
arrives before the units exist and is lost - then Python waited out the full 180 s.
`core/env.py` `start_battle()` now **resends the request every 4 s while the game is still
in setup** (`START_RESEND_SECONDS`), printing `[TabsEnv] battle did not start after 4s in
setup; resending start request (#N).` Tested offline against a fake mod that ignores the
first request; **not yet confirmed against the real game**. Counting those lines in
`training.log` shows how often starts really get ignored. Takes effect the next time training
starts (a running training process keeps the old code).

Still true: a battle that genuinely runs past 180 s scores a flat `0.0`, and scoring a
timeout from the last observed battle state is **not implemented**. Not done either: making
the mod apply the plan before handling the start request (the root cause of the race - needs a
rebuild), and the mod processing leftover `reset.json` / `start_battle.json` from the previous
session at launch.

**Placement vs the level.** The box is sized once by config, not measured from the level.
On an untuned level, 37% of units were moved off their requested spot and about 6% were
dropped. A possible fix (size the box automatically from the playable RED ground) is **not
built**.

**`training.log` encoding.** `Tee-Object` in PowerShell 5.1 writes UTF-16 with a BOM.
Restarting with different methods mixes encodings in one file and text tools then show
garbage or miss lines. The checkpoint (binary) is always the reliable source of progress.
Reading it from Python: decode as `utf-16le` if it starts with `\xff\xfe`, else `utf-8-sig`.

**The mod only reads its settings at launch.** Level resets and restarting training do not
reload them.

**A process can die silently.** TABS has closed with no error a couple of times during long
runs; the mod log just ends. Training then stops with no message. Check section 3 after
long absences.

**Old checkpoints from other code versions.** `ENCODING_VERSION` mismatch starts fresh. A
checkpoint from a different level or different placement settings will resume but is wrong.

**Search limits.** The 25 independent unit slots make it awkward to stack many copies of one
unit; single-unit spam does not win anyway on hard levels. PPO (`core/train_ppo.py`) exists
but has never been run end to end.

---

## 10. One-time setup (already done on this machine)

- **Python:** 3.12 venv in `.venv`. `requirements.txt` pins `numpy==2.2.6` because Windows
  Smart App Control blocks newer unsigned numpy builds. PyTorch is installed separately from
  the CUDA 12.8 index (needed for the RTX 5060):
  `pip install torch --index-url https://download.pytorch.org/whl/cu128`
- **BepInEx 5.4.23.5 (x64)** is unzipped into the TABS folder. TABS is a Mono game, so it
  must be BepInEx 5, not 6.
- **.NET 8 SDK** is installed, so the mod can be rebuilt without Visual Studio. The project
  references `Microsoft.NETFramework.ReferenceAssemblies` so `net472` builds without the
  Developer Pack.

Rebuild and deploy the mod (TABS must be **closed** to overwrite the DLL):

```powershell
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
cd C:\Users\bense\Downloads\Tabs_RLAgent_Testing\TabsAgentMod
dotnet build TabsAgentMod.csproj -c Debug
Copy-Item .\bin\Debug\net472\TabsAgentMod.dll "C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator\BepInEx\plugins\TabsAgentMod.dll" -Force
```

Then relaunch TABS and check the log for `TabsAgent Mod loaded.`

---

## 11. Archive status (as of 2026-10-15)

| Folder | Best cost | Placement settings (offset / box X / box Z) |
|---|---|---|
| `chal_level_1` | -1.236 | 4 / 17 / 30 - recorded automatically |
| `chal_level_2` | -1.088 | 4 / 17 / 30 - recorded automatically |
| `chal_level_2_A` | -1.107 | 4 / 17 / 30 - backfilled by hand |
| `chal_level_3` | -1.208 | 4 / 17 / 30 - recorded automatically |
| `chal_level_4` | -1.198 | 4 / 17 / 30 - recorded automatically |
| `adventure_level_1` | -1.167 | 15 / 40 / 40 - backfilled (inferred) |
| `level1_hardest_23020cost` | -0.775 (not converged, gen 73) | 30 / 40 / 40 - backfilled (from the log seen at the time) |

`adventure_level_2` and `adventure_level_3` were deleted (2026-10-15): they had no recorded
placement settings, so they couldn't be replayed reliably.

"Backfilled" entries carry `"backfilled": true` and a `basis` note in their
`best_army_meta.json`.
