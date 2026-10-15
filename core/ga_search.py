"""CMA-ES search that evolves armies against the currently loaded TABS level."""

import cma
import json
import numpy as np
import os
import pickle
import uuid
from pathlib import Path
from typing import List, Tuple

from core.env import TabsEnv
from core.unit_catalog import load_unit_catalog, placeable_unit_names

MAX_UNITS = 25
GENES_PER_UNIT = 3    # [unit_type_gene, x_gene, y_gene]
NUM_REEVALUATIONS = 3
POPULATION_REEVALUATIONS = 1
POPSIZE = 64
SIGMA0 = 1.0
POSITION_SPAN = 8.0
ENCODING_VERSION = 4
CHECKPOINT_PATH = Path("checkpoints/ga_latest.pkl")
BEST_ARMY_PLAN_PATH = Path("checkpoints/best_army_plan.json")
BEST_ARMY_META_PATH = Path("checkpoints/best_army_meta.json")
_last_budget = None


# Decode a vector into a list of placements of units on the map
def decode_individual(vector, unit_names: List[str], unit_catalog, budget: float) -> List[Tuple[str, float, float]]:
    placements = []
    cost_so_far = 0
    for i in range(MAX_UNITS):
        unit_type_gene = vector[i * GENES_PER_UNIT]
        x_gene = vector[i * GENES_PER_UNIT + 1]
        y_gene = vector[i * GENES_PER_UNIT + 2]
        name = unit_names[int(unit_type_gene) % len(unit_names)]
        x = float(np.clip(0.5 + x_gene / POSITION_SPAN, 0.0, 1.0))
        y = float(np.clip(0.5 + y_gene / POSITION_SPAN, 0.0, 1.0))
        cost = unit_catalog[name].cost
        if cost_so_far + cost > budget:
            continue
        cost_so_far += cost
        placements.append((name, x, y))
    return placements

# How well an army performs in a single evaluation
def evaluate(vector, env: TabsEnv, unit_names: List[str], unit_catalog) -> float:
    global _last_budget
    try:
        state = env.reset()
        budget = state["Budget"]
        _last_budget = budget
        placements = decode_individual(vector, unit_names, unit_catalog, budget)
        state, reward, done, info = env.place_army(placements)
        if done:
            return -reward
        state, reward, done, info = env.start_battle()
        return -reward
    except TimeoutError:
        print("evaluate() timed out - treating as a loss.", flush=True)
        return 0.0

#tabs is not deterministic, so we need to evaluate the same vector multiple times to smooth out the noise
def evaluate_averaged(vector, env: TabsEnv, unit_names: List[str], unit_catalog, num_trials: int = NUM_REEVALUATIONS) -> float:
    mean = 0
    for _ in range(num_trials):
        cost = evaluate(vector, env, unit_names, unit_catalog)
        mean += cost
    return mean / num_trials

MOD_CONFIG_PATH = Path(os.environ.get(
    "TABS_MOD_CONFIG_PATH",
    r"C:\Program Files (x86)\Steam\steamapps\common\Totally Accurate Battle Simulator"
    r"\BepInEx\config\com.tabsagent.mod.cfg",
))
PLACEMENT_CONFIG_KEYS = {
    "AutoAnchorBackOffset": float,
    "DeploymentBoxHalfExtentX": float,
    "DeploymentBoxHalfExtentZ": float,
    "IgnoreCursorForAutoAnchor": lambda text: text.strip().lower() == "true",
}


def read_placement_config() -> dict:
    config = {key: None for key in PLACEMENT_CONFIG_KEYS}
    try:
        text = MOD_CONFIG_PATH.read_text(encoding="utf-8-sig")
    except OSError as exc:
        print(f"Could not read mod config ({exc}); placement_config will be null in the saved meta.", flush=True)
        return {"source": str(MOD_CONFIG_PATH), **config}

    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or line.startswith("["):
            continue
        key, separator, value = line.partition("=")
        key = key.strip()
        if separator and key in PLACEMENT_CONFIG_KEYS:
            try:
                config[key] = PLACEMENT_CONFIG_KEYS[key](value)
            except ValueError:
                pass
    return {"source": str(MOD_CONFIG_PATH), **config}


def save_best_army(vector, cost: float, generation: int, unit_names: List[str], unit_catalog, budget) -> None:
    if budget is None:
        return
    placements = decode_individual(vector, unit_names, unit_catalog, budget)
    BEST_ARMY_PLAN_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(BEST_ARMY_PLAN_PATH, "w", encoding="utf-8") as f:
        json.dump(
            {
                "StateId": str(uuid.uuid4()),
                "Placements": [
                    {"Name": name, "X": float(x), "Y": float(y)} for name, x, y in placements
                ],
            },
            f,
            indent=2,
        )
    with open(BEST_ARMY_META_PATH, "w", encoding="utf-8") as f:
        json.dump(
            {
                "averaged_cost": float(cost),
                "reeval_battles": NUM_REEVALUATIONS,
                "generation": generation,
                "encoding_version": ENCODING_VERSION,
                "budget": budget,
                "unit_count": len(placements),
                "cost_spent": sum(unit_catalog[name].cost for name, _, _ in placements),
                "units": sorted({name for name, _, _ in placements}),
                "placement_config": read_placement_config(),
                "genes": [float(g) for g in vector],
            },
            f,
            indent=2,
        )


def main():
    io_dir = Path(os.environ["LOCALAPPDATA"]) / "TabsAgentModIO"
    env = TabsEnv(io_dir)
    unit_catalog = load_unit_catalog(Path("data/units_dummy.json"))
    unit_names = placeable_unit_names(unit_catalog)
    num_genes = MAX_UNITS * GENES_PER_UNIT
    half_span = POSITION_SPAN / 2.0
    lower = np.empty(num_genes)
    upper = np.empty(num_genes)
    lower[0::GENES_PER_UNIT] = 0.0
    upper[0::GENES_PER_UNIT] = len(unit_names) - 1e-9
    lower[1::GENES_PER_UNIT] = -half_span
    upper[1::GENES_PER_UNIT] = half_span
    lower[2::GENES_PER_UNIT] = -half_span
    upper[2::GENES_PER_UNIT] = half_span
    stds = np.empty(num_genes)
    stds[0::GENES_PER_UNIT] = len(unit_names) / 4.0
    stds[1::GENES_PER_UNIT] = POSITION_SPAN / 4.0
    stds[2::GENES_PER_UNIT] = POSITION_SPAN / 4.0

    cma_opts = {
        "CMA_stds": stds,
        "popsize": POPSIZE,
        "CMA_diagonal_decoding": 1,
        "bounds": [lower.tolist(), upper.tolist()],
    }

    checkpoint = None
    if CHECKPOINT_PATH.exists():
        with open(CHECKPOINT_PATH, "rb") as f:
            checkpoint = pickle.load(f)

    best_ever_cost = float("inf")
    best_ever_vector = None

    if checkpoint is not None and checkpoint.get("encoding_version") == ENCODING_VERSION:
        es = checkpoint["es"]
        generations = checkpoint["generations"]
        best_ever_cost = checkpoint.get("best_ever_cost", float("inf"))
        best_ever_vector = checkpoint.get("best_ever_vector")
        print(f"Resumed from checkpoint at generation {generations}.", flush=True)
        if best_ever_vector is not None:
            print(f"Best army so far has averaged cost {best_ever_cost:.2f}.", flush=True)
    else:
        if checkpoint is not None:
            stale = checkpoint.get("encoding_version", 1)
            print(
                f"Checkpoint uses encoding v{stale}, current is v{ENCODING_VERSION}. "
                "Its genes are on the old scale and cannot be reused, so starting fresh.",
                flush=True,
            )
        x0 = np.zeros(num_genes)
        x0[0::GENES_PER_UNIT] = len(unit_names) / 2.0
        es = cma.CMAEvolutionStrategy(x0, SIGMA0, cma_opts)
        generations = 0

    while not es.stop():
        generations += 1
        solutions = es.ask()
        costs = [
            evaluate_averaged(solution, env, unit_names, unit_catalog, num_trials=POPULATION_REEVALUATIONS)
            for solution in solutions
        ]
        es.tell(solutions, costs)
        es.disp()
        best_index = int(np.argmin(costs))
        best_cost_avg = evaluate_averaged(solutions[best_index], env, unit_names, unit_catalog)
        print(f"Generation {generations}: averaged best cost = {best_cost_avg:.2f}", flush=True)
        if best_cost_avg < best_ever_cost:
            best_ever_cost = best_cost_avg
            best_ever_vector = np.array(solutions[best_index])
            save_best_army(
                best_ever_vector, best_ever_cost, generations, unit_names, unit_catalog, _last_budget
            )
            print(
                f"New best army (averaged cost {best_ever_cost:.2f}) saved to {BEST_ARMY_PLAN_PATH}.",
                flush=True,
            )

        CHECKPOINT_PATH.parent.mkdir(parents=True, exist_ok=True)
        with open(CHECKPOINT_PATH, "wb") as f:
            pickle.dump(
                {
                    "es": es,
                    "generations": generations,
                    "encoding_version": ENCODING_VERSION,
                    "best_ever_cost": best_ever_cost,
                    "best_ever_vector": best_ever_vector,
                },
                f,
            )

        if best_cost_avg <= -1.0:
            print("Converged: found a consistently winning army.", flush=True)
            break


if __name__ == "__main__":
    main()