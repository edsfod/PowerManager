"""Adaptive Li‑ion charge‑current algorithm."""
from .models import BatteryState, ChargerConfig, DampingConfig, TempProfile
from .charger import (
    ChargeController,
    ChargeDecision,
    compute_adaptive_charge_current,
    compute_charge_decision,
)

__all__ = [
    "BatteryState",
    "ChargerConfig",
    "ChargeController",
    "ChargeDecision",
    "DampingConfig",
    "TempProfile",
    "compute_adaptive_charge_current",
    "compute_charge_decision",
]
