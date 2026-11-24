# PowerManager

用于 **MateBook 14 2024（`FLMH-XX`）** 在 **Windows 11** 卸载华为电脑管家后的轻量电源与电池管理工具：锁定 `75%–80%` 充电上限与单次临时冲高、调度“普通 / 保持运行”双模式睡眠、提供现代待机睡不下去时的休眠兜底与醒来汇总通知。

- **适用范围**：仅支持 Windows 11 与华为 MateBook 14 2024（`FLMH-XX`）。
- **明确不做**：不支持 Linux、macOS 或鸿蒙；不预先兼容其他品牌笔记本；不提供永久 100% 充电开关。

---

## 用法

通过桌面或开始菜单的 **PowerManager**（`PowerManager.lnk`）打开界面，可切换模式与发起单次临时冲高。平时处于“普通”模式；“保持运行”模式可定时 `2 / 4 / 8 / 12 / 24` 小时，到期自动切回普通模式。

| 行为 | 普通模式 | 保持运行模式（跑实验 / 通勤） |
|---|---|---|
| 合盖 | 无外接显示器时约 1 分钟后睡眠 | 继续运行 |
| 插电闲置 | 15 分钟关屏，1 小时睡眠 | 15 分钟关屏，不自动睡眠 |
| 用电池闲置 | 5 分钟关屏，15 分钟睡眠 | 5 分钟关屏，30 分钟睡眠；电池 ≥ 40 °C 提前睡眠 |
| 电源键 | 禁用（仅保留待机按压唤醒与长按约 10 秒硬件强制关机） | 同左 |
| 电源模式 | 插电 `Best Performance`；电池 ≥ 50% `Balanced`，< 50% `Best Power Efficiency` | 同左 |

- **休眠兜底**：主动要求睡眠后 2 分钟仍未进入睡眠阶段（如后台高负载），或合盖状态下意外唤醒后 2 分钟仍未睡下（“保持运行 + 插电”除外），自动强制休眠。后台高负载时程序无法强制进入现代待机，仅能休眠；若此时需要真睡，请通过开始菜单点击“睡眠”。
- **临时冲高**：可将上限临时调至 `85%–100%`，满足达标保持满设定时间（15–180 分钟）、拔电或超过最长兜底小时数（1–12 小时）后自动回落至 `75%–80%`。

### 当前状态与目标命令行约定

> 当前 `PowerManager-v2` 处于**文档体系与独立验证基线阶段**，主程序尚未迁入；按 [ADR-0005](docs/adr/0005-choose-csharp-framework-dependent.md)，主程序将采用 **C#（.NET 10 SDK，Framework-Dependent）** 重写。

各运行实体的目标命令行约定如下（可执行文件名随 C# 工程落地确定）：
- `agent`：`--status`（查看当前模式、系统设置与常驻状态）、`--restore-original`（停用并恢复 Windows 原始电源设置）；
- `battery-charge`：`--status`（查看充电状态；事件驱动下插电且电量不变时几小时不更新读数时间属正常现象）、`--boost 95 --hold-min 60 --max-hours 4`。

---

## 安装与依赖

- **主程序运行依赖（目标架构）**：Windows 11、`.NET 10 Desktop Runtime`（开发构建需 `.NET 10 SDK`）与系统自带 WebView2。
- **独立验证脚本依赖（`scripts/`）**：Python 3.13（`verify_wmi_readonly.py` 另需 `comtypes`）。
- **目标安装 / 卸载流程**：以管理员身份运行安装脚本（`install.ps1`）注册 `PowerManager-Agent` 与 `Battery-ChargeLimit` 计划任务、登记通知 `AppUserModelID` 与图标路径并生成桌面与开始菜单快捷方式 `PowerManager.lnk`（移动项目目录后需重新运行）。卸载时先执行 `agent --restore-original` 恢复 Windows 原电源设置，再运行安装脚本卸载参数移除计划任务、快捷方式与 `AppUserModelId` 注册表项。

---

## 核验与测试

- **主程序单元测试**：随 C# 工程落地后验证状态转移、闹钟计算、日志摘要与 `decide()` 等核心纯函数。
- **接口与文档独立校验（`scripts/`）**：
  - `python scripts/verify_power_boundaries.py check-s3`：核验系统 S0 / S3 睡眠支持状态；
  - `python scripts/verify_wmi_readonly.py`：只读核验 `BatteryStatus` 与（管理员下）EC WMI 传感器读数；
  - `python scripts/verify_doc_traceability.py`：校验需求文档与设计追踪矩阵的 100% 双向匹配。

---

## 文档导航

| 文档 | 用途 |
|---|---|
| [docs/requirements.md](docs/requirements.md) | 外部可验收的功能与非功能需求（`SLP-*` / `PWR-*` / `CHG-*` / `NTF-*` / `NFR-*`） |
| [docs/design.md](docs/design.md) | 系统架构分层、模块契约、状态持久化与需求追踪矩阵 |
| [docs/conventions.md](docs/conventions.md) | 高内聚边界、去硬编码、接口验证与文档归类规范 |
| [docs/adr/](docs/adr/) | 关键架构决策记录（单机型范围、休眠兜底、拒绝自适应充电、事件驱动迁移、C# .NET 10 技术栈选型） |
| [docs/reference/](docs/reference/) | 长期有效的硬件 WMI/EC 接口与 Windows 现代待机/Toast 机制参考 |
| [docs/investigations/](docs/investigations/) | 带时间戳的故障排查案卷 |
| [docs/porting-guide.md](docs/porting-guide.md) | 未来更换新 Windows 笔记本时的 5 步硬件移植指南 |
| [CHANGELOG.md](CHANGELOG.md) | 项目演进与变更历史 |
