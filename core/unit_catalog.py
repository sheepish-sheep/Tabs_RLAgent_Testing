"""Load unit data from JSON into Unit objects."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Dict

from core import models


def load_unit_catalog(path: Path) -> Dict[str, models.Unit]:
    with path.open("r", encoding="utf-8") as f:
        raw = json.load(f)

    catalog: Dict[str, models.Unit] = {}
    for item in raw:
        weapons_raw = item.get("weapons", [])
        weapons = [
            models.Weapon(
                name=weapon["name"],
                weapon_type=weapon["weapon_type"],
                damage_min=int(weapon["damage_min"]),
                damage_max=int(weapon["damage_max"]),
                cooldown_s=float(weapon["cooldown_s"]),
                dps=float(weapon["dps"]),
                range_wm=float(weapon["range_wm"]),
            )
            for weapon in weapons_raw
        ]
        unit = models.Unit(
            name=item["name"],
            cost=int(item["cost"]),
            health=int(item["health"]),
            speed=float(item["speed"]),
            attack=int(item["attack"]),
            weapon=item["weapon"],
            size=item["size"],
            attack_range=int(item["attack_range"]),
            description=item.get("description", ""),
            faction=item.get("faction", ""),
            abilities=list(item.get("abilities", [])),
            tags=list(item.get("tags", [])),
            weapons=weapons,
            faction_index=_to_optional_int(item.get("faction_index")),
            slot_index=_to_optional_int(item.get("slot_index")),
        )
        catalog[unit.name] = unit

    return catalog


def _to_optional_int(value):
    if value is None:
        return None
    return int(value)
