"""Budget-matched control experiment: how should a fixed number of noisy
evaluations be spent -- on reevaluations (current code) or on more candidates?

Surrogate objective mimics the real one: a 75-D landscape whose *true* value is
smooth, observed only through a battle with large per-battle noise.
Total budget is held FIXED at 3200 'battles' per run (= 100 gens of the current
16x2 scheme), matching ~36 generations x 35 battles of the live run scale.
"""
import numpy as np, cma

N = 75
BUDGET = 3200
NOISE = 0.5   # per-battle SD, conservative vs the live run


W = np.logspace(0, 1, N)
XOPT = np.random.default_rng(42).normal(0, 3.0, N)  # optimum a typical sigma0-distance away


_SCALE = float(np.sum(W * XOPT ** 2))  # so that true_f(x0=0) == 1.0


def true_f(x):
    # smooth, mildly ill-conditioned; normalised so f(x0)=1 and f(opt)=0,
    # i.e. the whole useful range is ~1.0, like the observed 0.5*kill_fraction term
    return float(np.sum(W * (np.asarray(x) - XOPT) ** 2)) / _SCALE


def battle(x, rng):
    return true_f(x) + rng.normal(0, NOISE)


def run(popsize, reevals, opts=None, seed=0):
    rng = np.random.default_rng(seed)
    o = {"popsize": popsize, "verbose": -9, "seed": seed + 1, "tolflatfitness": 1e9,
         "tolfun": 0, "tolx": 0}
    if opts:
        o.update(o | opts)
    es = cma.CMAEvolutionStrategy(np.zeros(N), 3.0, o)
    used = 0
    while used + popsize * reevals <= BUDGET:
        X = es.ask()
        F = [np.mean([battle(x, rng) for _ in range(reevals)]) for x in X]
        used += popsize * reevals
        es.tell(X, F)
    return true_f(es.mean), es.countiter, used


rows = []
for label, pop, reev, opts in [
    ("CURRENT: lam=16, 2 reevals", 16, 2, None),
    ("lam=16, 1 eval", 16, 1, None),
    ("lam=32, 1 eval", 32, 1, None),
    ("lam=64, 1 eval", 64, 1, None),
    ("lam=32, 1 eval + diag_decoding", 32, 1, {"CMA_diagonal_decoding": 1}),
    ("lam=64, 1 eval + diag_decoding", 64, 1, {"CMA_diagonal_decoding": 1}),
    ("lam=64, 1 eval + diag_dec + TPA", 64, 1, {"CMA_diagonal_decoding": 1,
                                                "AdaptSigma": cma.sigma_adaptation.CMAAdaptSigmaTPA}),
]:
    vals = [run(pop, reev, opts, seed=s)[0] for s in range(5)]
    r = run(pop, reev, opts, seed=0)
    rows.append((label, np.median(vals), r[1]))
    print(f"{label:34s} median true_f(mean) = {np.median(vals):.4f}   (gens={r[1]}, evals={r[2]})")

base = rows[0][1]
print()
for label, v, _ in rows:
    print(f"{label:34s} {base / v:6.2f}x better than current" if v > 0 else label)
print(f"\n(true_f at x0 = {true_f(np.zeros(N)):.4f}; true_f of a random sigma0=3 sample "
      f"= {np.median([true_f(np.random.default_rng(s).normal(0,3,N)) for s in range(50)]):.4f})")
