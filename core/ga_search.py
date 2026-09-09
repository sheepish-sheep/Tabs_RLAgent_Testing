"""GA/CMA-ES army-placement search (Phase 2) - evolves armies against a fixed,
already-loaded campaign level via TabsEnv, using pycma.
"""

import cma
import numpy as np
import os
import pickle
from pathlib import Path
from typing import List, Tuple

from core.env import TabsEnv
from core.unit_catalog import load_unit_catalog

MAX_UNITS = 25
GENES_PER_UNIT = 3    # [unit_type_gene, x_gene, y_gene]
NUM_REEVALUATIONS = 3  
POPULATION_REEVALUATIONS = 2 
CHECKPOINT_PATH = Path("checkpoints/ga_latest.pkl")  


# Decode a vector into a list of placements of units on the map
def decode_individual(vector, unit_names: List[str], unit_catalog, budget: float) -> List[Tuple[str, float, float]]:
    placements = []
    cost_so_far = 0
    for i in range(MAX_UNITS):
        unit_type_gene = vector[i * GENES_PER_UNIT]
        x_gene = vector[i * GENES_PER_UNIT + 1]
        y_gene = vector[i * GENES_PER_UNIT + 2]
        name = unit_names[int(unit_type_gene) % len(unit_names)]
        x = 1.0 / (1.0 + np.exp(-x_gene))
        y = 1.0 / (1.0 + np.exp(-y_gene))
        cost = unit_catalog[name].cost
        if cost_so_far + cost > budget:
            continue
        cost_so_far += cost
        placements.append((name, x, y))
    return placements

# How well an army performs in a single evaluation
def evaluate(vector, env: TabsEnv, unit_names: List[str], unit_catalog, stage) -> float:
    try:
        state = env.reset(stage)
        budget = state["Budget"]
        placements = decode_individual(vector, unit_names, unit_catalog, budget)
        state, reward, done, info = env.place_army(placements)
        if done:
            return -reward
        state, reward, done, info = env.start_battle()
        return -reward
    except TimeoutError:
        print("evaluate() timed out - treating as a loss.", flush=True)
        return 1.0

#tabs is not deterministic, so we need to evaluate the same vector multiple times to smooth out the noise
def evaluate_averaged(vector, env: TabsEnv, unit_names: List[str], unit_catalog, stage, num_trials: int = NUM_REEVALUATIONS) -> float:
    mean = 0
    for _ in range(num_trials):
        cost = evaluate(vector, env, unit_names, unit_catalog, stage)
        mean += cost
    return mean / num_trials

def main():
    io_dir = Path(os.environ["LOCALAPPDATA"]) / "TabsAgentModIO"
    env = TabsEnv(io_dir)
    unit_catalog = load_unit_catalog(Path("data/units_dummy.json"))
    unit_names = list(unit_catalog.keys())
    stage = None

    if CHECKPOINT_PATH.exists():
        with open(CHECKPOINT_PATH, "rb") as f:
            checkpoint = pickle.load(f)
        es = checkpoint["es"]
        generations = checkpoint["generations"]
        print(f"Resumed from checkpoint at generation {generations}.", flush=True)
    else:
        x0 = np.zeros(MAX_UNITS * GENES_PER_UNIT)
        sigma0 = 3.0
        stds = np.ones(MAX_UNITS * GENES_PER_UNIT)
        stds[0::GENES_PER_UNIT] = len(unit_names) / (2 * sigma0)
        es = cma.CMAEvolutionStrategy(x0, sigma0, {"CMA_stds": stds})
        generations = 0

    while not es.stop():
        generations += 1
        solutions = es.ask()
        costs = [
            evaluate_averaged(solution, env, unit_names, unit_catalog, stage, num_trials=POPULATION_REEVALUATIONS)
            for solution in solutions
        ]
        es.tell(solutions, costs)
        es.disp()
        best_index = int(np.argmin(costs))
        best_cost_avg = evaluate_averaged(solutions[best_index], env, unit_names, unit_catalog, stage)
        print(f"Generation {generations}: averaged best cost = {best_cost_avg:.2f}", flush=True)

        CHECKPOINT_PATH.parent.mkdir(parents=True, exist_ok=True)
        with open(CHECKPOINT_PATH, "wb") as f:
            pickle.dump({"es": es, "generations": generations}, f)

        if best_cost_avg <= -1.0:
            print("Converged: found a consistently winning army.", flush=True)
            break


if __name__ == "__main__":
    main()