"""Demo: single‑step calculation + closed‑loop charge simulation.

Run from the parent directory:  python -m charge_current_model.demo
"""
from charge_current_model import BatteryState, ChargerConfig, compute_charge_decision
from charge_current_model.sim import simulate

# Example scenario (single step)
state = BatteryState(
    soc=0.65,
    voltage=3.95,
    temp_c=30.0,
    soh=0.95,
    r_internal=0.05,
    i_prev=0.8,
)
cfg = ChargerConfig()

d = compute_charge_decision(state, cfg)
print(f"Single step: {d.current_a:.3f} A  (limited by: {d.reason})")

# Closed‑loop simulation
res = simulate(cfg, soc0=0.10)
charging = [i for i, c in enumerate(res.current) if c > 0]
end = charging[-1]
print(f"\nSimulated charge 10% -> {res.soc[end]*100:.1f}% in {res.t_s[end]/60:.1f} min")
print(f"Peak voltage {max(res.voltage):.4f} V (limit {res.v_limit[end]:.4f} V)")
print(f"Final state: {res.reason[-1]}")
print("\n  t[min]   SOC     V       I[A]   reason")
for k in range(0, end + 1, max(1, end // 15)):
    print(f"  {res.t_s[k]/60:6.1f}  {res.soc[k]:.3f}  {res.voltage[k]:.4f}  {res.current[k]:5.3f}  {res.reason[k]}")
