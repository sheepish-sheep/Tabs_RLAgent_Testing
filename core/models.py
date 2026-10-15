"""Unit data model loaded from the unit catalog."""

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class Unit:
    name: str
    cost: int
    health: int
    speed: float
    attack: int
    size: str
    attack_range: float
    description: str
    faction: str
    abilities: list[str] = field(default_factory=list)
    tags: list[str] = field(default_factory=list)
    faction_index: Optional[int] = None
    slot_index: Optional[int] = None
