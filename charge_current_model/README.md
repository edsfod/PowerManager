# charge_current_model

Antigravity 写的自适应充电电流算法，2026-11-22 审过，留作参考，不接入（原名 `battery_charger`，与 `battery-charge/` 太像，改名）。

审核结论：
- 代码本身干净：输入全部校验（含 NaN），恒流 → 降流 → 恒压、温度与老化降额、终止去抖与滞回都有，28 个测试全过。
- 用不到这台机器上：它每个周期算一个充电电流，而本机 EC 只能设启充、停充百分比，没有设电流的接口；恒流恒压由电池包和充电芯片在硬件上做。
- 默认参数也不合本机电池：它按单节 4.20 V、4.5 Ah 写，本机是 3 串、每串 4.48 V、约 70 Wh；真要用得先改 `v_max_new`、`capacity_ah`。
- 本机能做的“到某电量停、过热少充”，EC 阈值（`battery-charge/`）与 EC 自己的发热降档已经在做。

---


Adaptive Li‑ion charge‑current algorithm (CC → taper → CV, temperature / SOH derating).

- `compute_charge_decision(state, cfg)` – stateless, one sample → `ChargeDecision(current_a, reason, terminate)`
- `compute_adaptive_charge_current(state, cfg)` – stateless, returns A (0.0 = stop)
- `ChargeController(cfg).step(state)` – **use this in a real loop**: termination debounce, latching with SOC hysteresis, fault latching (`reset()` to clear)

> ⚠️ `ChargerConfig.v_max_new` defaults to **4.20 V**. Set 4.35 / 4.40 V only for high‑voltage cells per datasheet.

```powershell
cd ..   # parent of charge_current_model
python -m charge_current_model.demo
python -m unittest discover -s charge_current_model/tests -t .
```
