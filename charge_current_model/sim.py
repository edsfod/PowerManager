"""Minimal closed‑loop cell model for exercising :class:`ChargeController`.

Model: OCV(SOC) lookup + series resistance (no RC dynamics). Good enough to
verify voltage compliance, taper and termination behaviour.
"""
import random
from bisect import bisect_right
from dataclasses import dataclass, field

from .charger import ChargeController, ChargeDecision
from .models import BatteryState, ChargerConfig

# Generic 4.20 V NMC‑like OCV curve (SOC, V)
OCV_TABLE = [
    (0.00, 3.00), (0.05, 3.45), (0.10, 3.55), (0.20, 3.62), (0.40, 3.72),
    (0.60, 3.85), (0.80, 4.00), (0.90, 4.08), (1.00, 4.20),
]


def ocv_of_soc(soc: float) -> float:
    xs = [p[0] for p in OCV_TABLE]
    soc = min(1.0, max(0.0, soc))
    i = min(len(xs) - 1, max(1, bisect_right(xs, soc)))
    (x0, y0), (x1, y1) = OCV_TABLE[i - 1], OCV_TABLE[i]
    return y0 + (y1 - y0) * (soc - x0) / (x1 - x0)


@dataclass
class SimResult:
    t_s: list = field(default_factory=list)
    soc: list = field(default_factory=list)
    voltage: list = field(default_factory=list)
    current: list = field(default_factory=list)
    reason: list = field(default_factory=list)
    v_limit: list = field(default_factory=list)


def simulate(cfg: ChargerConfig, soc0: float = 0.1, temp_c: float = 25.0,
             soh: float = 1.0, r: float = 0.05, dt_s: float = 10.0,
             max_steps: int = 20000, rest_steps: int = 200,
             r_noise: float = 0.0, v_noise: float = 0.0, seed: int = 0) -> SimResult:
    """Run a charge until termination, then ``rest_steps`` more samples.

    ``r_noise`` is the relative std‑dev of Gaussian noise on the *estimated*
    internal resistance passed to the controller (the cell uses the true ``r``).
    ``v_noise`` is the std‑dev (V) of Gaussian voltage measurement noise.
    ``SimResult.voltage`` records the *true* terminal voltage.
    """
    rng = random.Random(seed)
    ctl = ChargeController(cfg)
    soc, i_prev = soc0, 0.0
    res = SimResult()
    done_at = None
    for k in range(max_steps):
        v = ocv_of_soc(soc) + i_prev * r
        r_est = max(1e-4, r * (1.0 + rng.gauss(0.0, r_noise))) if r_noise else r
        v_meas = v + rng.gauss(0.0, v_noise) if v_noise else v
        d: ChargeDecision = ctl.step(BatteryState(soc, v_meas, temp_c, soh, r_est, i_prev))
        res.t_s.append(k * dt_s); res.soc.append(soc); res.voltage.append(v)
        res.current.append(d.current_a); res.reason.append(d.reason); res.v_limit.append(d.v_limit)
        soc = min(1.0, soc + d.current_a * dt_s / 3600.0 / cfg.capacity_ah)
        i_prev = d.current_a
        if done_at is None and not ctl.is_charging:
            done_at = k
        if done_at is not None and k - done_at >= rest_steps:
            break
    return res
