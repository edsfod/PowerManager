# PowerManager

用于 **MateBook 14 2024（`FLMH-XX`）** 在 **Windows 11** 卸载华为电脑管家后的轻量电源与电池管理工具：

- 把充电上限锁定在 `75%–80%`，需要时可单次临时冲高；
- 提供“普通 / 保持运行”两种睡眠模式；
- 现代待机睡不下去时自动休眠兜底，开盖后弹出醒来汇总通知。

**适用范围**：仅支持 Windows 11 与华为 MateBook 14 2024（`FLMH-XX`）。不支持 Linux、macOS 或鸿蒙，不预先兼容其他品牌笔记本，不提供永久 100% 充电开关。

---

## 快速开始

**依赖**：Windows 11、`.NET 10 SDK`（运行只需 `.NET 10 Desktop Runtime`）、系统自带 WebView2。

1. **构建**：在仓库根目录运行
   `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`
   4 个可执行文件（`PowerManager.Agent.exe`、`PowerManager.BatteryCharge.exe`、`PowerManager.UI.exe`、`pmctl.exe`）会发布到 `dist/`，共享同一套 DLL。
2. **安装**：以**管理员身份**运行
   `powershell -ExecutionPolicy Bypass -File install.ps1`
   脚本会：
   - 注册计划任务 `PowerManager-Agent`（常驻调度）与 `Battery-ChargeLimit`（提权写充电阈值）；
   - 登记通知用的 `AppUserModelID`；
   - 在桌面与开始菜单创建 `PowerManager.lnk`。
3. **打开**：从桌面或开始菜单点 **PowerManager**。

补充说明：

- 运行时状态与日志存放在 `%LOCALAPPDATA%\PowerManager`，所有程序都支持用 `--data-dir` 覆盖（运行 `pmctl paths` 可查看具体文件位置）。
- 计划任务和快捷方式指向 `dist/` 的绝对路径，**移动项目目录后需重新运行 `install.ps1`**。
- 从 v1 迁移时，可加 `-ImportOriginalFrom <v1 的 original.json>` 只读导入原始电源设置快照。

---

## 用法

平时处于“普通”模式。“保持运行”模式可定时 `1–24` 小时（到期自动切回普通模式），也可选“直到手动改回”。

| 行为 | 普通模式 | 保持运行模式（跑实验 / 通勤） |
|---|---|---|
| 合盖 | 无外接显示器时约 1 分钟后睡眠 | 继续运行 |
| 插电闲置 | 15 分钟关屏，1 小时睡眠 | 15 分钟关屏，不自动睡眠 |
| 用电池闲置 | 5 分钟关屏，15 分钟睡眠 | 5 分钟关屏，30 分钟睡眠；电池 ≥ 40 °C 提前睡眠 |
| 电源键 | 禁用（仅保留待机按压唤醒与长按约 10 秒硬件强制关机） | 同左 |
| 电源模式 | 插电 `Best Performance`；电池 ≥ 50% `Balanced`，< 50% `Best Power Efficiency` | 同左 |

- **休眠兜底**：程序主动要求睡眠后 2 分钟仍未睡下（例如后台高负载），或合盖时意外唤醒后 2 分钟仍未睡下（“保持运行 + 插电”除外），会自动强制休眠。后台高负载时程序无法让系统进入现代待机，只能休眠；这时如果想真正睡眠，请在开始菜单点“睡眠”。
- **临时冲高**：把上限临时调到 `85%–100%`。达标后插电保持满设定时间（15–180 分钟）、达标后拔电、未达标拔电满 10 分钟，或超过最长时限（1–12 小时），任一条件满足即自动回落到 `75%–80%`。

完整的行为定义见 [docs/requirements.md](docs/requirements.md)。

### 命令行（`pmctl.exe`）

`pmctl` 以普通用户权限运行，不弹 UAC：

| 命令 | 作用 |
|---|---|
| `pmctl status` | 查看当前模式、系统电源设置、常驻状态与充电阈值 |
| `pmctl mode normal`<br>`pmctl mode keep-running --hours 12` | 切换模式（`--hours 0` 表示直到手动改回） |
| `pmctl boost 95 --hold-min 60 --max-hours 4` | 发起单次临时冲高 |
| `pmctl restore-original` | 恢复 Windows 原始电源设置并让常驻退出（**下次登录会重新启动**，见下方“暂停”） |
| `pmctl paths` | 列出数据目录与各状态/日志文件路径 |

`pmctl status` 显示的 EC 读数时间在插电且电量不变时可能几小时不更新，这是事件驱动设计的正常现象。管理员终端也可以直接运行 `PowerManager.BatteryCharge.exe --status` / `--boost <目标>` 读写 EC。

---

## 升级、暂停与卸载

以下命令都在**管理员** PowerShell 中、仓库根目录下执行。

**升级**：Agent 和界面运行时会锁住 `dist/` 里的文件，先停掉再发布（`publish.ps1` 检测到 `dist/` 中有程序在运行时会直接报错退出）：

1. 关闭 PowerManager 窗口，停止常驻：
   `Stop-ScheduledTask -TaskName PowerManager-Agent; Stop-Process -Name PowerManager.Agent -ErrorAction SilentlyContinue`
2. `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`
3. `powershell -ExecutionPolicy Bypass -File install.ps1`（重新注册并启动计划任务；已有的 `original.json` 和日志会保留）

**暂停**（临时交还 Windows 自己管理电源设置）：先 `Disable-ScheduledTask -TaskName PowerManager-Agent`，再 `pmctl restore-original`。恢复时执行 `Enable-ScheduledTask -TaskName PowerManager-Agent; Start-ScheduledTask -TaskName PowerManager-Agent`。暂停期间 `Battery-ChargeLimit` 仍会在开机、唤醒和插拔电时维持 `75%–80%` 上限。

**卸载**：先运行 `pmctl restore-original` 恢复 Windows 原始电源设置，再运行 `install.ps1 -Uninstall`，移除计划任务、快捷方式与 `AppUserModelId` 注册表项。`%LOCALAPPDATA%\PowerManager` 中的数据会保留。

---

## 常见问题

| 现象 | 处理 |
|---|---|
| 界面顶栏显示红点“常驻没在运行” | 点旁边的“启动”。仍然离线时，查看 `agent/error.log`，以及任务计划程序中 `PowerManager-Agent` 的上次运行结果 |
| 点“刷新读数”后 EC 更新时间仍不变 | 查看任务计划程序中 `Battery-ChargeLimit` 的上次运行结果：`0x2` 表示写阈值后读回不一致（`battery-charge/ec.log` 中有 `MISMATCH`）；`0x1` 表示其他异常（见 `battery-charge/error.log`） |
| 醒来通知没有弹横幅 | 锁屏期间的通知会推迟到解锁后再发。一直不弹时，检查开始菜单里是否有 `PowerManager.lnk`（横幅依赖它携带的 AppUserModelID），没有就重新运行 `install.ps1` |
| 移动项目目录后快捷方式或计划任务失效 | 重新运行 `install.ps1` |

---

## 核验与测试

- **单元与集成测试**：`dotnet test PowerManager.sln`（覆盖状态转移、闹钟计算、日志摘要、冲高决策、WMI 编解码与 Named Pipe 回环）。
- **独立校验脚本（`scripts/`，需要 Python 3.13；`verify_wmi_readonly.py` 另需 `comtypes`）**：
  - `python scripts/verify_power_boundaries.py check-s3`：核验系统 S0 / S3 睡眠支持状态；
  - `python scripts/verify_wmi_readonly.py`：只读核验 `BatteryStatus` 与（管理员下）EC WMI 传感器读数；
  - `python scripts/verify_doc_traceability.py`：校验需求文档与设计追踪矩阵的编号一一对应；
  - `python scripts/verify_ui_layout.py`：用系统 Edge 无头模式校验 UI 各视口无垂直溢出、固有高度合理。

---

## 文档导航

| 文档 | 用途 |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | 代码地图、改动红线、跨进程主链路、按任务最小阅读清单与工程/文档约定（改代码前先看这里） |
| [docs/requirements.md](docs/requirements.md) | 外部可验收的功能与非功能需求（`SLP-*` / `PWR-*` / `CHG-*` / `NTF-*` / `NFR-*`） |
| [docs/design.md](docs/design.md) | 架构分层、模块与 IPC 契约、状态文件字段与需求追踪矩阵 |
| [docs/ui-design.md](docs/ui-design.md) | 界面布局、窗口尺寸策略、草稿状态机与布局验收 |
| [docs/adr/](docs/adr/) | 关键架构决策记录 |
| [docs/reference/](docs/reference/) | 长期有效的硬件 WMI/EC 接口与 Windows 现代待机/Toast 机制参考 |
| [docs/porting-guide.md](docs/porting-guide.md) | 将来换新 Windows 笔记本时的硬件移植步骤 |
| [docs/roadmap.md](docs/roadmap.md) | 未排期的远期方向与架构前置条件 |
| [CHANGELOG.md](CHANGELOG.md) | 变更历史 |
