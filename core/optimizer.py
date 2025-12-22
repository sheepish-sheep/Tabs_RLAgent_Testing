"""Budget enforcement and plan validation for LLM output."""

from typing import Dict

from core.models import Army, Unit


class Optimizer:
    def __init__(self, unit_catalog: Dict[str, Unit]):
        self.unit_catalog = unit_catalog

    def __call__(self, plan: Dict[str, int], budget: int) -> Army:
        units = []
        for name, count in plan.items():
            unit = self.unit_catalog.get(name)
            if unit is None or count <= 0:
                continue
            units.extend([unit] * count)

        total_cost = sum(unit.cost for unit in units)
        if total_cost > budget:
            units.sort(key=lambda u: u.cost, reverse=True)
            while units and total_cost > budget:
                removed = units.pop(0)
                total_cost -= removed.cost

        return Army(units=units, total_cost=total_cost, total_size=len(units))
    
