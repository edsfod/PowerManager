# PowerManager

这台 MateBook 14 2024（`FLMH-XX`）的电源管理：充电上限与临时冲高；普通 / 保持运行两种模式下的睡眠、合盖、电源键、电源模式；睡不下去时的兜底；每次醒来的通知。要什么见 `REQUIREMENTS.md`，怎么做见 `DESIGN.md`。

## 用法

- 桌面或开始菜单的 **PowerManager**：切换模式（普通 / 保持运行 2、4、8、12、24 小时），临时冲高充电。
- 平时是普通模式；保持运行到时间自动回到普通模式。

| | 普通 | 保持运行（跑实验、通勤） |
|---|---|---|
| 合盖 | 1 分钟后睡 | 继续跑 |
| 插电闲置 | 15 分钟关屏，1 小时睡 | 15 分钟关屏，不睡 |
| 用电池闲置 | 5 分钟关屏，15 分钟睡 | 5 分钟关屏，30 分钟睡；电池 40 °C 提前睡 |
| 电源键 | 不起作用（睡着时按会唤醒；按住约 10 秒强制关机是硬件做的） | 同左 |
| 电源模式 | 插电 Best Performance；用电池时电量 ≥ 50% Balanced，< 50% Best Power Efficiency | 同左 |

让它睡了 2 分钟还醒着（后台忙，或在包里被唤醒后睡不下去），就休眠。后台忙时程序没法让它真睡，只能休眠（`REQUIREMENTS.md`“实测”）；这时想真睡，只能自己在开始菜单点“睡眠”。

```powershell
python "$env:USERPROFILE\Desktop\PowerManager\agent\agent.py" --status              # 模式、设置、常驻在不在
python "$env:USERPROFILE\Desktop\PowerManager\battery-charge\battery_charge.py" --status   # 充电
python "$env:USERPROFILE\Desktop\PowerManager\agent\agent.py" --restore-original    # 停用，恢复 Windows 原设置
```

## 组成

| 东西 | 在哪 | 怎么跑 |
|---|---|---|
| 常驻：收电源通知（合盖、亮屏关屏、插拔电、电量）、按模式改设置、闹钟、兜底、醒来通知 | `agent/`（`agent.py` 管 Windows，`brain.py` 做决定） | 计划任务 `PowerManager-Agent`，登录时起，用户权限，崩了自动重启；不轮询 |
| EC 充电上限 75%–80% 与临时冲高 | `battery-charge/`，见它的 `README.md` | 计划任务 `Battery-ChargeLimit`，管理员，跑一次就退出：开机、登录、醒来、插拔电时，以及常驻叫它时 |
| 界面 | `ui/web/index.html`（页面，WebView2 里显示），`ui/ui.py`（后端），图标 `ui/icon.*` | 桌面与开始菜单 `PowerManager.lnk`；`python ui\ui.py --debug` 带开发者工具 |
| 共用：EC 的 WMI 调用、电源设置、系统日志、通知、路径 | `common/` | |
| 安装 | `install.ps1`（管理员） | 改了任务、快捷方式、路径后重跑 |
| 笔记 | `notes/`：电池 WMI 接口反编译、换电池后耗电快、醒来通知的流程与坑 | |
| 自适应充电电流算法（不接入，理由在它的 `README.md`） | `charge_current_model/` | |

依赖（conda-forge 或 pip）：`comtypes`（WMI、计划任务、快捷方式）、`pywebview`（界面，连带 `pythonnet`；用系统自带的 WebView2）。

安装：管理员 PowerShell 里 `powershell -ExecutionPolicy Bypass -File install.ps1`（默认用 `PATH` 上的 `python.exe`，也可用 `-Python <路径>` 指定）。它注册两个任务，删掉旧的 `PowerManager-ModeWatch`，登记通知发送者 `PowerManager`，建快捷方式并启动。

## 核实

- `agent.py --status`：`running: True`，设置与上表一致。
- 合盖 3 分钟再开盖、解锁：弹一条“睡了 X 分钟”之类的通知；`agent/var/agent.log` 有对应的 `wake:` 行。
- 充电：见 `battery-charge/README.md`“核实”。
- 测试：`agent/` 里 `python -m unittest test_brain`；`battery-charge/` 里 `python -m unittest test_battery_charge`。

## 数据

`var/` 不进 git，也不在任何备份里。

| 路径 | 类 | 是什么 |
|---|---|---|
| `agent/var/mode.json` | 保留 | 当前模式与保持运行到几点 |
| `agent/var/original.json` | 保留 | 第一次改动前的 Windows 设置，`--restore-original` 用 |
| `agent/var/agent.log`（`.1`） | 保留 | 常驻做的每件事、收到的合盖 / 屏幕 / 电源通知、醒来通知的内容（UTC）；超过 1 MB 轮换 |
| `agent/var/error.log` | 保留 | 出错时的 traceback |
| `agent/var/request.json` | 缓存 | 界面发给常驻的切换请求，读后即删 |
| `battery-charge/var/` | | 见 `battery-charge/README.md` |

## 被别的程序按路径读的

- 计划任务 `PowerManager-Agent` 读 `agent/agent.py`；`Battery-ChargeLimit` 读 `battery-charge/battery_charge.py`；两个 `PowerManager.lnk` 读 `ui/ui.py`、`ui/icon.ico`；注册表 `HKCU\Software\Classes\AppUserModelId\PowerManager` 读 `ui/icon.png`。挪动这些路径后重跑 `install.ps1`。

## 来历

`battery-charge/` 与电池接口反编译笔记 2026-11-22 从 Config（`topics/machine/windows/`）移出，之前的历史在 Config 的 git 里（`09bfa6e` 及以前）；Config 那边的规定与状态检查同日删去，两个项目不再互相读。同日重写为事件驱动：旧的 `power-mode/`（每 30 秒检查一次）与 `battery-charge` 每 5 分钟一次的定时触发都已去掉，见 `DESIGN.md`。
