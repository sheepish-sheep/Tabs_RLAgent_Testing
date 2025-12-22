"""Rule-based army builder and counter heuristics."""

from typing import Dict, List

from core import models
from core import optimizer


class StrategyRules:
    def __init__(self, unit_catalog: dict[str, models.Unit]):
        self.unit_catalog = unit_catalog
        self.optimizer = optimizer(unit_catalog)


    def __call__(self, budget: int, enemy_army: models.Army | None = None) -> models.Army:
        scored = []
        for unit in self.unit_catalog.values():
            score = (unit.attack + unit.health) / max(unit.cost, 1)
            scored.append((score, unit))
        scored.sort(key=lambda x: x[0], reverse=True)
        plan = {}
        remaining = budget
        for _, unit in scored:
            count = remaining // unit.cost
            if count > 0:
                plan[unit.name] = count
                remaining -= count * unit.cost
        return self.optimizer(plan, budget)
