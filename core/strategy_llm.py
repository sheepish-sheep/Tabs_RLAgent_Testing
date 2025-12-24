"""LLM-driven strategy provider using LangChain with structured output."""

from __future__ import annotations

import json
from typing import Dict, List, Optional

from langchain_core.prompts import ChatPromptTemplate
from langchain_openai import ChatOpenAI
from pydantic import BaseModel, Field

from core import models


class Placement(BaseModel):
    name: str = Field(..., description="Unit name from catalog")
    x: float = Field(..., ge=0.0, le=1.0, description="Normalized X in battlefield [0,1]")
    y: float = Field(..., ge=0.0, le=1.0, description="Normalized Y in battlefield [0,1]")


class ArmyPlan(BaseModel):
    placements: List[Placement]


class StrategyLLM:
    def __init__(self, model=None):
        self.model = model or ChatOpenAI(model="gpt-4o")
        self.structured_model = self.model.with_structured_output(ArmyPlan)
        self.prompt = ChatPromptTemplate.from_messages(
            [
                (
                    "system",
                    "You build TABS armies within a strict budget. "
                    "Only use units from the catalog. Return JSON only. "
            "You are a trained strategist who builds the best army possible. THIS MEANS TAKE INTO ACCOUNT YOUR ENEMY'S ARMY WHEN BUILDING YOUR OWN. COUNTER PICK "
                    "You are always the RED team; place units only on the RED side. "
                    "Use normalized placement coordinates (x, y) in [0,1] relative to the battlefield.(Do not reuse the same coordinates) "
                    "All placement patterns must be relative to the pre-placed RED anchor unit that defines the front line; use it as the only positional reference. "
                    "Spread placements across X/Z, avoid duplicate coordinates, and use formations (line/arc)."
                    "Placing multiple copies of the same unit is allowed. "
                    "Use y for depth: lower y = back line, higher y = front line. Place ranged/support behind melee/tanks."
                    "Also remember that Tabs has friendly fire and units will often hit allies. "
                    "Never exceed the budget and never invent unit names. "
                    "Prefer a balanced mix using tags (melee, ranged, tank, aoe) when possible."
                    "However, depending on enemy number and type, all ranged, all AoE, or all melee could be fine"
                    "Remember their is normally no wrong answer so hoardes of cheap units are acceptable. "
                    "Spend at least 95% of budget unless impossible; minimize leftover without exceeding it."
                ),
                (
                    "human",
                    "Mode: {mode}\n"
                    "Budget: {budget}\n"
                    "Target spend: {target_spend}\n"
                    "Unit catalog (name, cost, tags): {unit_catalog}\n"
                    "Enemy army (name, count): {enemy_summary}\n"
                    "Constraints: use only catalog units; total cost <= budget; "
                    "placements must be on RED side only.\n"
                    "Return a JSON object with a top-level 'placements' array.",
                ),
            ]
        )

    def __call__(self, unit_catalog: Dict[str, models.Unit], budget: int,
        enemy_army: Optional[models.Army] = None,) -> ArmyPlan:
        catalog_list = [
            {"name": unit.name, "cost": unit.cost, "tags": list(getattr(unit, "tags", []))}
            for unit in unit_catalog.values()
        ]
        summary = summarize_army(enemy_army)
        target_spend = compute_target_spend(unit_catalog, enemy_army, budget)
        messages = self.prompt.format_messages(
            mode="counter" if enemy_army is not None else "place",
            budget=budget,
            target_spend=target_spend,
            unit_catalog=json.dumps(catalog_list),
            enemy_summary=json.dumps(summary),
        )
        result = self.structured_model.invoke(messages)
        if isinstance(result, ArmyPlan):
            return result
        return ArmyPlan.model_validate(result)


def summarize_army(army: Optional[models.Army]) -> List[Dict[str, int]]:
    if army is None or not army.units:
        return []
    counts: Dict[str, int] = {}
    for unit in army.units:
        counts[unit.name] = counts.get(unit.name, 0) + 1
    return [{"name": name, "count": count} for name, count in counts.items()]


def normalize_name(name: str) -> str:
    if not name:
        return ""
    return "".join(char.upper() for char in name if char.isalnum())


def compute_target_spend(
    unit_catalog: Dict[str, models.Unit],
    enemy_army: Optional[models.Army],
    budget: int,
) -> int:
    if enemy_army is None or not enemy_army.units:
        return budget
    by_name = {unit.name: unit.cost for unit in unit_catalog.values()}
    by_norm = {normalize_name(unit.name): unit.cost for unit in unit_catalog.values()}
    total = 0
    matched = 0
    for unit in enemy_army.units:
        name = getattr(unit, "name", None)
        if not name:
            continue
        cost = by_name.get(name)
        if cost is None:
            cost = by_norm.get(normalize_name(name))
        if cost is None:
            continue
        total += cost
        matched += 1
    if matched == 0:
        return budget
    return min(budget, total)
