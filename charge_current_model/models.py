import math
from dataclasses import dataclass, field, fields


def _check_finite(obj: object, prefix: str) -> None:
    """Ensure every numeric field on a dataclass instance is finite."""
    for f in fields(obj):
        v = getattr(obj, f.name)
        if isinstance(v, (int, float)) and not isinstance(v, bool) and not math.isfinite(v):
            raise ValueError(f"{prefix}.{f.name} must be finite")


@dataclass
class BatteryState:
    """Current battery measurements.

    Attributes
    ----------
    soc: float
        State of charge, range 0.0 – 1.0.
    voltage: float
        Measured single‑cell terminal voltage (V), sampled while ``i_prev``
        was flowing.
    temp_c: float
        Cell temperature in degrees Celsius.
    soh: float
        State of health, 0.0 – 1.0 (new battery = 1.0).
    r_internal: float
        Estimated DC internal resistance (Ω). Must be > 0.
    i_prev: float = 0.0
        *Measured* current of the previous sampling period (A).
        Positive = charging, negative = discharging (used for OCV estimation).
    is_charging: bool = True
        Charging enable flag (used for hysteresis by the stateless API;
        :class:`~charge_current_model.charger.ChargeController` manages it itself).
    """
    soc: float
    voltage: float
    temp_c: float
    soh: float
    r_internal: float
    i_prev: float = 0.0
    is_charging: bool = True


@dataclass
class TempProfile:
    """JEITA‑style temperature windows and thermal derating parameters."""
    t_min_charge: float = 0.0           # below → no charging (°C)
    t_cold_end: float = 10.0            # [t_min, t_cold_end) → c_rate_cold (°C)
    t_normal_start: float = 20.0        # linear ramp cold → max until here (°C)
    c_rate_cold: float = 0.2            # C‑rate cap in the cold window (C)
    t_hot_derate_start: float = 38.0    # start of high‑temp derating (°C)
    t_max_charge: float = 45.0          # above → no charging (°C)
    hot_factor_at_max: float = 0.3      # c_rate_max multiplier at t_max_charge
    v_hot_derate: float = 0.05          # V removed from v_limit at t_max_charge (V)

    def __post_init__(self) -> None:
        _check_finite(self, "temp")
        if not (self.t_min_charge < self.t_cold_end <= self.t_normal_start
                <= self.t_hot_derate_start < self.t_max_charge):
            raise ValueError("temperature thresholds must be strictly ordered")
        if self.c_rate_cold <= 0:
            raise ValueError("temp.c_rate_cold must be positive")
        if not (0 < self.hot_factor_at_max <= 1):
            raise ValueError("temp.hot_factor_at_max must be within (0, 1]")
        if self.v_hot_derate < 0:
            raise ValueError("temp.v_hot_derate must be >= 0")


@dataclass
class DampingConfig:
    """Control‑loop damping, ramp‑rate limiting, and termination debounce."""
    ramp_up_c_per_step: float = 0.25    # max current increase per step (C)
    cv_approach_gain: float = 0.3       # fraction of gap closed per step when
                                        # rising toward the CV ceiling (1.0 = no smoothing)
    cv_minor_ov_band: float = 0.01      # V; overvoltage below this is "minor"
    cv_minor_ov_inertia: float = 0.0    # weight of i_prev on minor overvoltage
                                        # (0.0 = drop to CV ceiling at once; original used 0.2)
    cv_band: float = 0.02               # V below v_limit considered "in CV"
    term_debounce_steps: int = 3        # consecutive steps before terminating

    def __post_init__(self) -> None:
        _check_finite(self, "damping")
        if self.ramp_up_c_per_step <= 0:
            raise ValueError("damping.ramp_up_c_per_step must be positive")
        if not (0 < self.cv_approach_gain <= 1):
            raise ValueError("damping.cv_approach_gain must be within (0, 1]")
        if not (0 <= self.cv_minor_ov_inertia < 1):
            raise ValueError("damping.cv_minor_ov_inertia must be within [0, 1)")
        if self.cv_minor_ov_band < 0:
            raise ValueError("damping.cv_minor_ov_band must be >= 0")
        if self.cv_band < 0:
            raise ValueError("damping.cv_band must be >= 0")
        if self.term_debounce_steps < 1:
            raise ValueError("damping.term_debounce_steps must be >= 1")


@dataclass
class ChargerConfig:
    """Configuration parameters for the charger algorithm.

    Top‑level fields cover cell datasheet specifications and charging strategy;
    thermal windows and loop damping live in :attr:`temp` and :attr:`damping`.
    Defaults target a generic 4.5 Ah, 4.20 V graphite / NMC Li‑ion cell.

    .. warning::
       ``v_max_new`` **must** match the cell datasheet. High‑voltage cells
       use 4.35 V / 4.40 V; using those values on a standard 4.20 V cell
       will overcharge it.
    """
    # --- Cell specifications -----------------------------------------------
    capacity_ah: float = 4.5            # nominal capacity (Ah)
    v_max_new: float = 4.20             # max charge voltage for a fresh cell (V)
    c_rate_max: float = 1.5             # maximum allowed charge C‑rate
    c_rate_cutoff: float = 0.03         # termination C‑rate
    v_precharge: float = 3.0            # precharge threshold (V, OCV)
    c_rate_precharge: float = 0.1       # C‑rate when OCV < v_precharge
    v_overvoltage_fault: float = 0.10   # V above v_limit that forces a stop

    # --- SOC strategy & aging compensation ---------------------------------
    hard_cap_soc: float = 1.0           # hard stop SOC (e.g. 0.80 for longevity)
    resume_soc_gap: float = 0.05        # hysteresis gap for restart
    taper_start_soc: float = 0.75       # SOC where tapering begins
    taper_k: float = 3.5                # exponential taper steepness
    soft_land_at_hard_cap: bool = True  # smooth soft‑landing at hard cap
    v_soh_derate: float = 0.05          # V removed from v_max at SOH = 0
    taper_soh_shift: float = 0.10       # taper start shift at SOH = 0

    # --- Sub‑configurations ------------------------------------------------
    temp: TempProfile = field(default_factory=TempProfile)
    damping: DampingConfig = field(default_factory=DampingConfig)

    def __post_init__(self) -> None:
        _check_finite(self, "cfg")
        if not isinstance(self.temp, TempProfile):
            raise TypeError("cfg.temp must be a TempProfile instance")
        if not isinstance(self.damping, DampingConfig):
            raise TypeError("cfg.damping must be a DampingConfig instance")
        if self.capacity_ah <= 0:
            raise ValueError("cfg.capacity_ah must be positive")
        if not (0 < self.v_precharge < self.v_max_new):
            raise ValueError("require 0 < v_precharge < v_max_new")
        if not (0 < self.c_rate_cutoff < self.temp.c_rate_cold <= self.c_rate_max):
            raise ValueError("require 0 < c_rate_cutoff < temp.c_rate_cold <= c_rate_max")
        if not (0 < self.c_rate_precharge <= self.c_rate_max):
            raise ValueError("require 0 < c_rate_precharge <= c_rate_max")
        if self.v_overvoltage_fault <= 0:
            raise ValueError("cfg.v_overvoltage_fault must be positive")
        if not (0 < self.hard_cap_soc <= 1):
            raise ValueError("cfg.hard_cap_soc must be within (0, 1]")
        if not (0 < self.resume_soc_gap < self.hard_cap_soc):
            raise ValueError("cfg.resume_soc_gap must be within (0, hard_cap_soc)")
        if not (0 <= self.taper_start_soc <= 1):
            raise ValueError("cfg.taper_start_soc must be within [0, 1]")
        if self.taper_k <= 0:
            raise ValueError("cfg.taper_k must be positive")
        if self.v_soh_derate < 0 or self.taper_soh_shift < 0:
            raise ValueError("v_soh_derate and taper_soh_shift must be >= 0")
