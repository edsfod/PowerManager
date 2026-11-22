"""Adaptive Li‑ion charge‑current computation.

Two APIs are provided:

* :func:`compute_charge_decision` / :func:`compute_adaptive_charge_current` –
  pure, stateless, one sampling period at a time.
* :class:`ChargeController` – stateful wrapper that adds termination
  debouncing, termination latching with SOC hysteresis and fault latching.
  Use this one in a real charger loop.
"""
import math
from dataclasses import dataclass, replace
from typing import Optional

from .models import BatteryState, ChargerConfig

# Decision reasons
R_FAULT = "fault"              # invalid / non‑finite input
R_FAULT_LATCHED = "fault_latched"
R_OVERVOLTAGE = "overvoltage"  # terminal voltage far above the limit
R_SOC_CAP = "soc_cap"          # SOC reached hard cap
R_HYSTERESIS = "hysteresis"    # stopped, waiting for SOC to drop
R_TEMP = "temperature"         # outside charging temperature window
R_PRECHARGE = "precharge"
R_CC = "cc"                    # limited by C‑rate ceiling
R_TAPER = "taper"              # limited by SOC taper curve
R_CV = "cv"                    # limited by voltage (CV) ceiling
R_RAMP = "ramp"                # limited by ramp‑up rate
R_DONE = "done"                # terminated (controller only)

# Internal algorithmic guardrails (not user‑tunable parameters)
_MIN_TAPER_SPAN = 1e-4         # numerical guard against division by ~0
_TAPER_MIN_SPAN = 0.15         # minimum SOC width reserved for the taper region
_TAPER_MIN_START_SOC = 0.10    # lower floor on taper start SOC under heavy aging
_HOT_FLOOR_CUTOFF_MULT = 1.5   # keeps warm C‑rate ceiling > cutoff to avoid false stop
_TERM_SOC_BAND = 0.01          # SOC margin (1 %) below target considered "near target"


@dataclass(frozen=True)
class ChargeDecision:
    """Result of one sampling period.

    ``terminate`` is a *termination candidate*: the stateless API stops
    immediately, :class:`ChargeController` debounces it first.
    """
    current_a: float
    reason: str
    terminate: bool = False
    v_limit: Optional[float] = None


def _validate(state: BatteryState) -> None:
    """Validate measurements; raise ValueError on illegal values.

    Every numeric field must be finite – a NaN from a broken sensor would
    otherwise silently pass all ``<`` / ``>`` comparisons.
    """
    for name in ("soc", "voltage", "temp_c", "soh", "r_internal", "i_prev"):
        if not math.isfinite(getattr(state, name)):
            raise ValueError(f"state.{name} must be finite")
    if not (0.0 <= state.soc <= 1.0):
        raise ValueError("state.soc must be within [0.0, 1.0]")
    if not (0.0 <= state.soh <= 1.0):
        raise ValueError("state.soh must be within [0.0, 1.0]")
    if state.voltage <= 0.0:
        raise ValueError("state.voltage must be positive")
    if state.r_internal <= 0.0:
        raise ValueError("state.r_internal must be positive")


def _hard_stop(state: BatteryState, cfg: ChargerConfig) -> Optional[str]:
    """Return a reason string if charging must be stopped immediately."""
    if state.soc >= cfg.hard_cap_soc:
        return R_SOC_CAP
    if not state.is_charging and state.soc > (cfg.hard_cap_soc - cfg.resume_soc_gap):
        return R_HYSTERESIS
    if state.temp_c < cfg.temp.t_min_charge or state.temp_c > cfg.temp.t_max_charge:
        return R_TEMP
    return None


def _estimate_ocv(state: BatteryState) -> float:
    """Estimate open‑circuit voltage by removing the IR drop of ``i_prev``."""
    return state.voltage - state.i_prev * state.r_internal


def _limit_voltage(state: BatteryState, cfg: ChargerConfig) -> float:
    """Maximum allowed terminal voltage (V), derated by SOH and temperature."""
    tp = cfg.temp
    v_limit = cfg.v_max_new - (1.0 - state.soh) * cfg.v_soh_derate
    if state.temp_c > tp.t_hot_derate_start:
        frac = (state.temp_c - tp.t_hot_derate_start) / (tp.t_max_charge - tp.t_hot_derate_start)
        v_limit -= tp.v_hot_derate * min(1.0, frac)
    return v_limit


def _temp_c_rate_limit(state: BatteryState, cfg: ChargerConfig) -> float:
    """Temperature‑dependent C‑rate ceiling.

    * ``[t_min, t_cold_end)``            → ``c_rate_cold`` (avoid Li plating)
    * ``[t_cold_end, t_normal_start)``   → linear ``c_rate_cold`` → ``c_rate_max``
    * ``[t_normal_start, t_hot_start]``  → ``c_rate_max``
    * ``(t_hot_start, t_max]``           → linear down to ``hot_factor_at_max``
    """
    tp = cfg.temp
    t = state.temp_c
    if t < tp.t_cold_end:
        return tp.c_rate_cold
    if t < tp.t_normal_start:
        frac = (t - tp.t_cold_end) / (tp.t_normal_start - tp.t_cold_end)
        return tp.c_rate_cold + (cfg.c_rate_max - tp.c_rate_cold) * frac
    if t <= tp.t_hot_derate_start:
        return cfg.c_rate_max
    frac = min(1.0, (t - tp.t_hot_derate_start) / (tp.t_max_charge - tp.t_hot_derate_start))
    factor = 1.0 - (1.0 - tp.hot_factor_at_max) * frac
    # Never drop below the cutoff, otherwise the charge would "terminate"
    # merely because it is warm.
    return max(cfg.c_rate_cutoff * _HOT_FLOOR_CUTOFF_MULT, cfg.c_rate_max * factor)


def _target_end_soc(cfg: ChargerConfig) -> float:
    return cfg.hard_cap_soc if cfg.soft_land_at_hard_cap else 1.0


def _base_c_rate(state: BatteryState, ocv: float, c_max_temp: float,
                 cfg: ChargerConfig) -> tuple[float, str]:
    """Target C‑rate before CV / ramp limiting, plus the limiting reason.

    The taper curve is exponential between ``effective_taper_soc`` and
    ``target_end_soc``, landing exactly on ``c_rate_cutoff``.
    """
    if ocv < cfg.v_precharge:
        return min(cfg.c_rate_precharge, c_max_temp), R_PRECHARGE

    target_end_soc = _target_end_soc(cfg)
    base_taper_soc = min(cfg.taper_start_soc, target_end_soc - _TAPER_MIN_SPAN)
    effective_taper_soc = max(_TAPER_MIN_START_SOC,
                              base_taper_soc - (1.0 - state.soh) * cfg.taper_soh_shift)

    if state.soc < effective_taper_soc:
        return c_max_temp, R_CC

    span = max(_MIN_TAPER_SPAN, target_end_soc - effective_taper_soc)
    progress = min(1.0, max(0.0, (state.soc - effective_taper_soc) / span))
    k = cfg.taper_k
    decay = (math.exp(-k * progress) - math.exp(-k)) / (1.0 - math.exp(-k))
    c = cfg.c_rate_cutoff + (c_max_temp - cfg.c_rate_cutoff) * decay
    return min(c, c_max_temp), R_TAPER


def _cv_limit(state: BatteryState, ocv: float, v_limit: float,
              i_target: float, cfg: ChargerConfig) -> float:
    """CV ceiling with asymmetric damping (restores the original design).

    Let ``i_cv_raw`` be the current at which terminal voltage == v_limit.

    * overvoltage >= ``cv_minor_ov_band`` → ``i_cv_raw`` immediately.
    * 0 < overvoltage < band → blend with ``cv_minor_ov_inertia`` of
      ``i_prev`` (default 0 = immediate; original firmware used 0.2).
    * no overvoltage **and** CV is the binding limit (``i_cv_raw <
      i_target``) → approach ``i_cv_raw`` by ``cv_approach_gain`` per step.
      Because no overvoltage ⇔ ``i_prev <= i_cv_raw``, the result lies in
      ``[i_prev, i_cv_raw]``: it rises smoothly and never exceeds the
      ceiling, suppressing current jitter from voltage measurement noise.
    * otherwise → ``i_cv_raw`` (CV not binding).

    Note: because OCV keeps rising during the sampling period, the next
    measured voltage may overshoot by ≈ ``dt·I·dOCV/dSOC`` (a few mV at
    dt = 10 s). A hardware CV loop should remain the primary voltage guard.
    """
    dmp = cfg.damping
    i_prev = max(0.0, state.i_prev)
    i_cv_raw = max(0.0, (v_limit - ocv) / state.r_internal)
    overvoltage = state.voltage - v_limit

    if overvoltage >= dmp.cv_minor_ov_band:
        return i_cv_raw
    if overvoltage > 0.0:
        a = dmp.cv_minor_ov_inertia
        return a * i_prev + (1.0 - a) * i_cv_raw
    if i_prev > 0.0 and i_cv_raw < i_target:
        g = dmp.cv_approach_gain
        return (1.0 - g) * i_prev + g * i_cv_raw
    return i_cv_raw


def compute_charge_decision(state: BatteryState, cfg: ChargerConfig) -> ChargeDecision:
    """Compute the charge current for one sampling period (stateless).

    Raises ``ValueError`` on invalid measurements.
    """
    # 0️⃣ Validation
    _validate(state)

    # 1️⃣ Hard stops (SOC limit, hysteresis, temperature)
    reason = _hard_stop(state, cfg)
    if reason:
        return ChargeDecision(0.0, reason)

    # 2️⃣ OCV and voltage limit; gross over‑voltage is a stop condition
    ocv = _estimate_ocv(state)
    v_limit = _limit_voltage(state, cfg)
    if state.voltage > v_limit + cfg.v_overvoltage_fault:
        return ChargeDecision(0.0, R_OVERVOLTAGE, v_limit=v_limit)

    # 3️⃣ Temperature ceiling and SOC taper
    c_max_temp = _temp_c_rate_limit(state, cfg)
    target_c, reason = _base_c_rate(state, ocv, c_max_temp, cfg)
    i_final = target_c * cfg.capacity_ah

    # 4️⃣ CV ceiling with asymmetric damping
    i_cv = _cv_limit(state, ocv, v_limit, i_final, cfg)
    if i_cv < i_final:
        i_final, reason = i_cv, R_CV

    # 5️⃣ Ramp‑up limit (only restricts increases)
    i_ramp = max(0.0, state.i_prev) + cfg.damping.ramp_up_c_per_step * cfg.capacity_ah
    if i_ramp < i_final:
        i_final, reason = i_ramp, R_RAMP

    # 6️⃣ Termination candidate (near target SOC or in CV, and low current)
    i_cutoff = cfg.c_rate_cutoff * cfg.capacity_ah
    in_cv = (v_limit - state.voltage) <= cfg.damping.cv_band
    near_target = state.soc >= (_target_end_soc(cfg) - _TERM_SOC_BAND)
    terminate = (near_target or in_cv) and i_final <= i_cutoff

    return ChargeDecision(i_final, reason, terminate, v_limit)


def compute_adaptive_charge_current(state: BatteryState, cfg: ChargerConfig) -> float:
    """Stateless convenience API: current in A, ``0.0`` when charging must stop.

    Termination happens on the first candidate (no debouncing / latching);
    prefer :class:`ChargeController` in a real control loop.
    """
    d = compute_charge_decision(state, cfg)
    return 0.0 if d.terminate else d.current_a


class ChargeController:
    """Stateful charge controller.

    Adds on top of :func:`compute_charge_decision`:

    * **Termination debouncing** – ``cfg.damping.term_debounce_steps``
      consecutive candidates are required.
    * **Termination latching** – after stopping, charging resumes only when
      SOC has dropped ``resume_soc_gap`` below the stop SOC (or hard cap),
      preventing stop/start pulsing as voltage relaxes.
    * **Fault latching** – invalid input or gross over‑voltage latch a fault;
      call :meth:`reset` after the cause has been cleared.
    """

    def __init__(self, cfg: ChargerConfig):
        self.cfg = cfg
        self.reset()

    def reset(self) -> None:
        self.is_charging = True
        self.fault: Optional[str] = None
        self._term_count = 0
        self._stop_soc: Optional[float] = None

    def _stop(self, soc: float) -> None:
        self.is_charging = False
        self._stop_soc = soc
        self._term_count = 0

    def step(self, state: BatteryState) -> ChargeDecision:
        """Process one sample. ``state.is_charging`` is ignored."""
        cfg = self.cfg
        if self.fault:
            return ChargeDecision(0.0, R_FAULT_LATCHED)

        if not self.is_charging:
            soc = state.soc if math.isfinite(state.soc) else 1.0
            stop_soc = self._stop_soc if self._stop_soc is not None else cfg.hard_cap_soc
            if soc > min(cfg.hard_cap_soc, stop_soc) - cfg.resume_soc_gap:
                return ChargeDecision(0.0, R_DONE)
            self.is_charging = True
            self._term_count = 0

        try:
            d = compute_charge_decision(replace(state, is_charging=True), cfg)
        except ValueError as e:
            self.fault = str(e)
            return ChargeDecision(0.0, R_FAULT)

        if d.reason == R_OVERVOLTAGE:
            self.fault = f"overvoltage: {state.voltage:.3f} V > limit {d.v_limit:.3f} V"
            return d
        if d.reason == R_SOC_CAP:
            self._stop(state.soc)
            return d
        if d.terminate:
            self._term_count += 1
            if self._term_count >= cfg.damping.term_debounce_steps:
                self._stop(state.soc)
                return ChargeDecision(0.0, R_DONE, v_limit=d.v_limit)
        else:
            self._term_count = 0
        return d
