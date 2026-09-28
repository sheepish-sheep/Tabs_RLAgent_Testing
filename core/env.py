"""Gym-style environment wrapper over the TabsAgentMod file bridge.

Talks to the same state.json/plan.json/reset.json files the C# mod
(TabsAgentMod) reads and writes.
"""

from __future__ import annotations

import json
import time
import uuid
from pathlib import Path
from typing import Any, Dict, List, Tuple

STATE_FILE = "state.json"
PLAN_FILE = "plan.json"
RESET_FILE = "reset.json"
START_BATTLE_FILE = "start_battle.json"

DEFAULT_TIMEOUT_SECONDS = 180.0 
POLL_INTERVAL_SECONDS = 0.05
RETRY_POLL_TIMEOUT_SECONDS = 5.0
BATTLE_TIMEOUT_SECONDS = 180.0
RESET_TIMEOUT_SECONDS = 60.0 
WRITE_RETRY_ATTEMPTS = 5
WRITE_RETRY_DELAY_SECONDS = 0.05
OWN_SURVIVAL_WEIGHT = 0.25
RESET_SETTLE_SECONDS = 1.5

# How long to sit in the setup phase after asking for a battle before asking again. The
# game's StartGame() silently does nothing unless both teams already have units placed (and
# the placement intro / brush routine are idle), and the mod consumes each request file only
# once -- so a start request that lands before the plan is applied (they are written ~0.09 s
# apart, close to the mod's 0.1 s poll) is lost for good. The "no new state" resend below
# never fires for this, because state.json is rewritten every 0.1 s.
START_RESEND_SECONDS = 4.0


class TabsEnv:

    def __init__(self, io_dir: Path):
        self.io_dir = Path(io_dir)
        self.state_path = self.io_dir / STATE_FILE
        self.plan_path = self.io_dir / PLAN_FILE
        self.reset_path = self.io_dir / RESET_FILE
        self.start_battle_path = self.io_dir / START_BATTLE_FILE
        self._last_state_mtime = 0.0
        self._enemy_starting_count = 0
        self._enemy_starting_cost = 0
        # Denominator for own_survival. The mod exposes no own-side cost field,
        # so survival has to be measured by unit count against what we placed.
        self._own_starting_count = 0
        self._own_count_checked = False

    def reset(self, stage) -> Dict[str, Any]:
        deadline = time.time() + RESET_TIMEOUT_SECONDS
        self._write_json(self.reset_path, {"RequestId": str(uuid.uuid4())})
        last_enemy_count = None
        stable_since = None
        while True:
            got_new_state = False
            try:
                state = self._wait_for_new_state(timeout=RETRY_POLL_TIMEOUT_SECONDS)
                got_new_state = True
                if state.get("Phase") == "setup":
                    enemy_count = len(state.get("EnemyUnits", []))
                    now = time.time()
                    if enemy_count != last_enemy_count:
                        last_enemy_count = enemy_count
                        stable_since = now
                    elif now - stable_since >= RESET_SETTLE_SECONDS:
                        self._enemy_starting_count = sum(
                            unit.get("Count", 1) for unit in state.get("EnemyUnits", [])
                        )
                        self._enemy_starting_cost = state.get("EnemyStartingCost", 0)
                        # Nothing is placed yet this episode; step()/place_army()
                        # set the real value. Clearing it here stops a stale count
                        # from the previous episode leaking into this one.
                        self._own_starting_count = 0
                        return state
                else:
                    last_enemy_count = None
                    stable_since = None
            except TimeoutError:
                pass
            if time.time() > deadline:
                raise TimeoutError(
                    f"Timed out waiting for reset to reach setup phase after {RESET_TIMEOUT_SECONDS}s."
                )
            if not got_new_state:
                self._write_json(self.reset_path, {"RequestId": str(uuid.uuid4())})


    def step(self, action: Tuple[str, float, float]) -> Tuple[Dict[str, Any], float, bool, Dict[str, Any]]:
        name, x, y = action
        payload = {"StateId": str(uuid.uuid4()), "Placements": [{"Name": name, "X": float(x), "Y": float(y)}]}
        self._own_starting_count += 1
        self._write_json(self.plan_path, payload)
        state = self._wait_for_new_state()
        return state, *self._reward_done_info(state)

    def place_army(self, placements: List[Tuple[str, float, float]]) -> Tuple[Dict[str, Any], float, bool, Dict[str, Any]]:
        payload = {
            "StateId": str(uuid.uuid4()),
            "Placements": [{"Name": name, "X": float(x), "Y": float(y)} for name, x, y in placements],
        }
        self._own_starting_count = len(placements)
        self._write_json(self.plan_path, payload)
        state = self._wait_for_new_state()
        self._log_own_count_once(state)
        return state, *self._reward_done_info(state)

    def start_battle(self) -> Tuple[Dict[str, Any], float, bool, Dict[str, Any]]:
        deadline = time.time() + BATTLE_TIMEOUT_SECONDS
        self._write_json(self.start_battle_path, {"RequestId": str(uuid.uuid4())})
        last_sent = time.time()
        resends = 0
        while True:
            got_new_state = False
            try:
                state = self._wait_for_new_state(timeout=RETRY_POLL_TIMEOUT_SECONDS)
                got_new_state = True
                if state.get("MatchOver"):
                    return state, *self._reward_done_info(state)
                # Still in placement well after asking for a battle means the request was
                # silently ignored, so ask again instead of waiting out the full timeout.
                if state.get("Phase") == "setup" and time.time() - last_sent >= START_RESEND_SECONDS:
                    resends += 1
                    print(
                        f"[TabsEnv] battle did not start after {time.time() - last_sent:.0f}s in setup; "
                        f"resending start request (#{resends}).",
                        flush=True,
                    )
                    self._write_json(self.start_battle_path, {"RequestId": str(uuid.uuid4())})
                    last_sent = time.time()
            except TimeoutError:
                pass
            if time.time() > deadline:
                raise TimeoutError(
                    f"Timed out waiting for MatchOver after {BATTLE_TIMEOUT_SECONDS}s."
                )
            if not got_new_state:

                self._write_json(self.start_battle_path, {"RequestId": str(uuid.uuid4())})
                last_sent = time.time()

    def _wait_for_new_state(self, timeout: float = DEFAULT_TIMEOUT_SECONDS) -> Dict[str, Any]:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.state_path.exists():
                mtime = self.state_path.stat().st_mtime
                if mtime > self._last_state_mtime:
                    state = self._try_read_state()
                    if state is not None:
                        self._last_state_mtime = mtime
                        return state
            time.sleep(POLL_INTERVAL_SECONDS)
        raise TimeoutError(f"Timed out waiting for {self.state_path} to update.")
            

    def _try_read_state(self) -> Dict[str, Any] | None:
        try:
            with open(self.state_path, "r", encoding="utf-8-sig") as f:
                content = f.read()
                dict_state = json.loads(content)
                return dict_state
        except (json.JSONDecodeError, OSError):
            return None

    @staticmethod
    def _write_json(path: Path, payload: Dict[str, Any]) -> None:
        last_error = None
        for _ in range(WRITE_RETRY_ATTEMPTS):
            try:
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(payload, f, indent=2)
                return
            except (PermissionError, OSError) as e:
                last_error = e
                time.sleep(WRITE_RETRY_DELAY_SECONDS)
        raise last_error


    def _log_own_count_once(self, state: Dict[str, Any]) -> None:
        if self._own_count_checked:
            return
        self._own_count_checked = True
        reported = sum(unit.get("Count", 1) for unit in (state.get("YourUnits") or []))
        print(
            flush=True,
        )

    def _reward_done_info(self, state: Dict[str, Any]) -> Tuple[float, bool, Dict[str, Any]]:
        winner = state.get("Winner")
        done = bool(state.get("MatchOver"))
        if not done:
            return 0.0, False, {}
        else:
            enemy_remaining_cost = state.get("EnemyUnitsRemainingCost") or 0
            cost_killed = max(0, self._enemy_starting_cost - enemy_remaining_cost)
            enemy_destroyed = (
                min(1.0, cost_killed / self._enemy_starting_cost)
                if self._enemy_starting_cost > 0
                else 0.0
            )
            # Count-based, not cost-based: the mod exposes no own-side cost field.
            your_remaining = state.get("YourUnitsRemaining") or 0
            own_survival = (
                min(1.0, your_remaining / self._own_starting_count)
                if self._own_starting_count > 0 else 0.0
            )

            reward = enemy_destroyed
            # Only credit survivors on an actual win. On a loss this term is 0
            # anyway, but gating it means a draw or timer-ending battle cannot
            # score above a real victory.
            if winner == "Red":
                reward += OWN_SURVIVAL_WEIGHT * own_survival

            enemy_remaining = state.get("EnemyUnitsRemaining") or 0
            enemies_killed = max(0, self._enemy_starting_count - enemy_remaining)
            return reward, True, {
                "winner": winner,
                "enemy_destroyed": enemy_destroyed,
                "own_survival": own_survival,
                "enemies_killed": enemies_killed,
                "enemy_starting_count": self._enemy_starting_count,
                "cost_killed": cost_killed,
                "enemy_starting_cost": self._enemy_starting_cost,
            }