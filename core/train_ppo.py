from pathlib import Path
import os

from stable_baselines3 import PPO

from core.env import TabsEnv
from core.ppo_env import TabsGymEnv
from core.unit_catalog import load_unit_catalog

def main():
    io_dir = Path(os.environ["LOCALAPPDATA"]) / "TabsAgentModIO"
    tabs_env = TabsEnv(io_dir)
    unit_catalog = load_unit_catalog(Path("data/units_dummy.json"))
    unit_names = list(unit_catalog.keys())
    stage = None

    env = TabsGymEnv(tabs_env, unit_names, unit_catalog, stage)
    model = PPO("MlpPolicy", env, n_steps=64, verbose=1)
    model.learn(total_timesteps=1280)
    model.save("ppo_tabs_model")

if __name__ == "__main__":
    main()