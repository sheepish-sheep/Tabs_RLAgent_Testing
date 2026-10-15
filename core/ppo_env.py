"""Gymnasium wrapper around TabsEnv for Stable-Baselines3 PPO.
One action per step places a unit (type, x bin, y bin) or, with the extra type index, starts the battle."""

import gymnasium as gym
from gymnasium import spaces
import numpy as np

from core.env import TabsEnv

X_BINS = 20
Y_BINS = 20
NUM_ENEMY_POSITION_FEATURES = 4  # mean_x, mean_y, std_x, std_y

class TabsGymEnv(gym.Env):
    def __init__(self, tabs_env: TabsEnv, unit_names, unit_catalog):
        super().__init__()
        self.tabs_env = tabs_env
        self.unit_names = unit_names
        self.unit_catalog = unit_catalog
        self.starting_budget = None  # set on reset(), needed to normalize budget in observations
        self.unit_name_to_index = {name: i for i, name in enumerate(unit_names)}

        self.action_space = spaces.MultiDiscrete([len(unit_names) + 1, X_BINS, Y_BINS])
        obs_size = len(unit_names) + NUM_ENEMY_POSITION_FEATURES + 1
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(obs_size,), dtype=np.float32)

    def _encode_observation(self, state):
        counts = np.zeros(len(self.unit_names), dtype=np.float32)
        for enemy in state['EnemyUnits']:
            if enemy['Name'] in self.unit_name_to_index:
                counts[self.unit_name_to_index[enemy["Name"]]] += enemy["Count"]

        if state["EnemyUnits"]:
            xs = np.array([enemy["X"] for enemy in state["EnemyUnits"]], dtype=np.float32)
            ys = np.array([enemy["Y"] for enemy in state["EnemyUnits"]], dtype=np.float32)
            pos_features = [xs.mean(), ys.mean(), xs.std(), ys.std()]
        else:
            pos_features = [0.0, 0.0, 0.0, 0.0]
        normalized_budget = state["Budget"] / self.starting_budget if self.starting_budget else 0.0
        return np.concatenate([counts, pos_features, [normalized_budget]]).astype(np.float32)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        state = self.tabs_env.reset()
        self.starting_budget = state["Budget"]
        obs = self._encode_observation(state)
        return obs, {}

    def step(self, action):
        unit_index, x_bin, y_bin = action
        if unit_index == len(self.unit_names):
            state, reward, done, info = self.tabs_env.start_battle()
        else:
            name = self.unit_names[unit_index]
            x = x_bin / (X_BINS - 1)
            y = y_bin / (Y_BINS - 1)
            state, reward, done, info = self.tabs_env.step((name, x, y))
        obs = self._encode_observation(state)
        terminated = done
        truncated = False
        return obs, reward, terminated, truncated, info
