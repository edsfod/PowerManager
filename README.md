# PowerManager

这台 MateBook 14 2024（`FLMH-XX`）的电源管理：电池充电上限，以后还有睡眠、关机等电源设置（待办在 `PR.md`）。

| 东西 | 在哪 |
|---|---|
| 充电上限 75%–80% 与临时冲高（计划任务 `Battery-ChargeLimit`、桌面与开始菜单 `Battery Charge.lnk`） | `battery-charge/`，用法与核实见它的 `README.md` |
| 普通 / 保持运行（跑实验、通勤）两种电源模式（`Power Mode.lnk`、计划任务 `PowerManager-ModeWatch`） | `power-mode/`，见它的 `README.md` |
| 电池充电 WMI 接口的反编译与实测 | `notes/电池充电接口反编译-20261111.md` |
| 换电池后耗电快的排查 | `notes/换电池后耗电-20261122.md` |
| 自适应充电电流算法（Antigravity 写的，没接入，理由在 `battery-charge/README.md`“原理”） | `battery_charger/` |
| 用户的待办 | `PR.md` |

来历：`battery-charge/` 与反编译笔记 2026-11-22 从 Config（`topics/machine/windows/`）移出，之前的历史在 Config 的 git 里（`09bfa6e` 及以前）。Config 那边只留“电池充电上限”这条规定与它的状态检查（读 `battery-charge/var/state.json`）。

被别的程序按路径读的：计划任务 `Battery-ChargeLimit` 读 `battery-charge/battery_charge.py`（工作目录 `battery-charge/`）；两个 `Battery Charge.lnk` 读同一脚本与 `battery-charge/icon.ico`；Config 的 `topics/machine/windows/tools/state.ps1`、`probe.ps1` 读 `battery-charge/var/state.json`。挪动这些路径时，重跑 `battery-charge/install_task.ps1`（管理员），改两个快捷方式与 Config 里那两处。
