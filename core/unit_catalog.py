"""Load unit data from JSON into Unit objects."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Dict, List

from core import models

# Real units missing from this install's unit database, so the mod can never place them.
UNRESOLVABLE_UNITS = frozenset({
    "Celestial Aegis",
    "Chronomancer",
    "Death Bringer",
    "Divine Arbiter",
    "Exiled Sentinel",
    "Righteous Paladin",
    "Shadow Walker",
    "Tempest Lich",
    "Void Cultist",
    "Void Monarch",
})


def load_unit_catalog(path: Path) -> Dict[str, models.Unit]:
    with path.open("r", encoding="utf-8-sig") as f:
        raw = json.load(f)

    catalog: Dict[str, models.Unit] = {}
    for item in raw:
        unit = models.Unit(
            name=item["name"],
            cost=int(item["cost"]),
            health=int(item["health"]),
            speed=float(item["speed"]),
            attack=int(item["attack"]),
            size=item["size"],
            attack_range=float(item["attack_range"]),
            description=item.get("description", ""),
            faction=item.get("faction", ""),
            abilities=list(item.get("abilities", [])),
            tags=list(item.get("tags", [])),
            faction_index=_to_optional_int(item.get("faction_index")),
            slot_index=_to_optional_int(item.get("slot_index")),
        )
        catalog[unit.name] = unit

    return catalog


def placeable_unit_names(catalog: Dict[str, models.Unit]) -> List[str]:
    """Catalog unit names the mod can actually place, in catalog order."""
    return [name for name in catalog if name not in UNRESOLVABLE_UNITS]


def _to_optional_int(value):
    if value is None:
        return None
    return int(value)
