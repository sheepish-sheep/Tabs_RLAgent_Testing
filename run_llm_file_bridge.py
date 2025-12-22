"""File-based LLM bridge: reads state.json, writes plan.json."""

from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path
from typing import Dict, List, Optional

from core import models
from core.strategy_llm import StrategyLLM
from core.unit_catalog import load_unit_catalog

STATE_FILE = "state.json"
PLAN_FILE = "plan.json"


def load_env_file(path: Path) -> None:
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        entry = line.strip()
        if not entry or entry.startswith("#") or "=" not in entry:
            continue
        key, value = entry.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


def main() -> None:
    once = "--once" in sys.argv
    io_dir_arg = None
    if "--io-dir" in sys.argv:
        idx = sys.argv.index("--io-dir")
        if idx + 1 < len(sys.argv):
            io_dir_arg = sys.argv[idx + 1]
    io_dir = Path(io_dir_arg or os.getenv("TABS_AGENT_IO_DIR", str(default_io_dir())))
    io_dir.mkdir(parents=True, exist_ok=True)
    state_path = io_dir / STATE_FILE
    plan_path = io_dir / PLAN_FILE

    base_dir = Path(__file__).resolve().parent
    load_env_file(base_dir / ".env")
    unit_catalog = load_unit_catalog(base_dir / "data" / "units_dummy.json")
    llm = StrategyLLM()

    if once:
        run_once(state_path, plan_path, unit_catalog, llm)
        return

    last_state_mtime = 0.0
    print(f"Watching {state_path}")

    while True:
        if state_path.exists():
            mtime = state_path.stat().st_mtime
            if mtime > last_state_mtime:
                state = read_state(state_path)
                budget = int(state.get("Budget") or state.get("budget") or 0)
                enemy_army = build_enemy_army(state, unit_catalog)
                if budget <= 0 and enemy_army is not None:
                    budget = enemy_army.total_cost
                plan = llm(unit_catalog, budget, enemy_army=enemy_army)
                placements = trim_to_budget(plan.placements, unit_catalog, budget)
                state_id = state.get("StateId") or state.get("state_id") or ""
                write_plan(plan_path, str(state_id), placements)
                last_state_mtime = mtime
                print(f"Wrote {plan_path}")
        time.sleep(0.5)


def default_io_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or os.getcwd()
    return Path(base) / "TabsAgentModIO"


def read_state(path: Path) -> Dict[str, object]:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def build_enemy_army(
    state: Dict[str, object], unit_catalog: Dict[str, models.Unit]
) -> Optional[models.Army]:
    enemy_units = state.get("EnemyUnits") or state.get("enemy_units") or []
    units: List[models.Unit] = []
    for entry in enemy_units:
        if not isinstance(entry, dict):
            continue
        name = entry.get("Name") or entry.get("name")
        count = int(entry.get("Count") or entry.get("count") or 0)
        if not isinstance(name, str):
            continue
        unit = unit_catalog.get(name)
        if unit is None or count <= 0:
            continue
        units.extend([unit] * count)
    if not units:
        return None
    total_cost = sum(unit.cost for unit in units)
    return models.Army(units=units, total_cost=total_cost, total_size=len(units))


def trim_to_budget(placements, unit_catalog, budget) -> List[object]:
    total = 0
    kept = []
    for item in placements:
        unit = unit_catalog.get(item.name)
        if unit is None:
            continue
        if total + unit.cost > budget:
            continue
        kept.append(item)
        total += unit.cost
    return kept


def write_plan(path: Path, state_id: str, placements) -> None:
    payload = {
        "StateId": state_id,
        "Placements": [
            {"Name": item.name, "X": float(item.x), "Y": float(item.y)}
            for item in placements
        ],
    }
    with path.open("w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)


def run_once(
    state_path: Path,
    plan_path: Path,
    unit_catalog: Dict[str, models.Unit],
    llm: StrategyLLM,
) -> None:
    if not state_path.exists():
        print(f"Missing {state_path}")
        return
    state = read_state(state_path)
    budget = int(state.get("Budget") or state.get("budget") or 0)
    enemy_army = build_enemy_army(state, unit_catalog)
    if budget <= 0 and enemy_army is not None:
        budget = enemy_army.total_cost
    plan = llm(unit_catalog, budget, enemy_army=enemy_army)
    placements = trim_to_budget(plan.placements, unit_catalog, budget)
    state_id = state.get("StateId") or state.get("state_id") or ""
    write_plan(plan_path, str(state_id), placements)
    print(f"Wrote {plan_path}")


if __name__ == "__main__":
    main()
