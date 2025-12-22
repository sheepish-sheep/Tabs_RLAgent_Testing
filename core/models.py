"""Core domain models and shared types for the game and detector outputs."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, List, Optional, Tuple


class Phase(Enum):
    SETUP = "setup"
    BATTLE = "battle"

class Color(Enum):
    RED = "red"

@dataclass
class Weapon:
    name: str
    weapon_type: str
    damage_min: int
    damage_max: int
    cooldown_s: float
    dps: float
    range_wm: float

@dataclass
class Unit:
    name: str
    cost: int
    health: int
    speed: int
    attack: int
    weapon: str
    size: str
    attack_range: int
    description: str
    faction: str
    abilities: list[str] = field(default_factory=list)
    tags: list[str] = field(default_factory=list)
    weapons: list[Weapon] = field(default_factory=list)
    faction_index: Optional[int] = None
    slot_index: Optional[int] = None


@dataclass
class Army:
    units: list[Unit] = field(default_factory=list)
    total_cost: int = field(default=0)
    total_size: int = field(default=0)

@dataclass
class GameState:
    phase: Phase
    player_budget: int
    enemy_budget: Optional[int] = None
    your_army: Optional[Army] = None
    enemy_army: Optional[Army] = None
