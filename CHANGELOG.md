# 变更日志（Changelog）

本文件按时间倒序记录项目的架构演进、规则变更与历史迁移。

---

## v2.0.0 — 2026-11-24（C# .NET 10 重写完成并切换至生产）
- **架构与 IPC 落地（[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md)）**：实现 `PowerManager.Core`、`PowerManager.Platform`、`PowerManager.Hardware.MateBook` 三层共享类库与 `PowerManager.Agent.exe`、`PowerManager.BatteryCharge.exe`、`PowerManager.UI.exe`、`pmctl.exe` 四个独立可执行文件；采用单用户 SID ACL Named Pipe（`UI / pmctl ↔ Agent`）与 `<DataDir>/requests/*.json` 原子白名单请求队列（`→ Battery-ChargeLimit` S4U 计划任务），运行时数据统一迁至 `%LOCALAPPDATA%\PowerManager`。
- **交互与通知完善**：`PowerManager.UI` 基于 WinForms + WebView2 实现单实例防多开、按需刷新 EC 读数、主动睡眠状态提示、系统深浅色标题栏热切换、纯前端本地倒计时与数据目录快捷入口；WinRT Toast 补齐静音（`<audio silent="true"/>`）、`Tag`/`Group` 去重覆盖与圆形图标。
- **发布、验证与切换**：新增统一 Framework-Dependent 发布脚本（`scripts/publish.ps1`）与零 Python 安装脚本（`install.ps1`，支持 `-ImportOriginalFrom` 只读导入 v1 `original.json`）；86 项单元测试与 19/19 需求双向追踪全部通过，实机完成生产切换与模式切换、EC 刷新、合盖诱导睡眠与醒来 Toast 验收。
- **远期规划**：新增 [docs/roadmap.md](docs/roadmap.md) 沉淀高频遥测、自适应调度、电池健康统计、新机型适配与交互式双向 Toast 五项远期演进方向。

## 2026-11-24（文档体系与验证基线重构）
- **独立重构目录**：在 `PowerManager-v2` 中重建清晰的正交文档体系，彻底剥离需求、设计、决策、参考事实与排查历史。
- **区分无时间性与有时间性资料**：将长期有效的硬件/平台事实收拢至 `docs/reference/`（[wmi-ec-interface.md](docs/reference/wmi-ec-interface.md)、[windows-standby-and-toast.md](docs/reference/windows-standby-and-toast.md)）；将带日期的故障案卷归入 `docs/investigations/`。
- **建立决策与规范体系**：沉淀 5 份架构决策记录（`docs/adr/0001`–`0005`，含 C# .NET 10 技术栈选型）、工程与文档边界规范（[docs/conventions.md](docs/conventions.md)）及新机型移植指南（[docs/porting-guide.md](docs/porting-guide.md)）。
- **需求追踪与独立验证脚本**：将需求改为按域前缀编号（`SLP-*` / `PWR-*` / `CHG-*` / `NTF-*` / `NFR-*`），在 [docs/design.md](docs/design.md) 建立需求追踪矩阵，并新增 `scripts/verify_power_boundaries.py`、`scripts/verify_wmi_readonly.py` 与 `scripts/verify_doc_traceability.py`。

## 2026-11-22（事件驱动重写与独立成库）
- **仓库独立**：`battery-charge/` 与 WMI 反编译笔记从原 Config 仓库（`topics/machine/windows/`）迁出独立成库（迁出前历史保留在 Config 仓库 `09bfa6e` 及之前提交）。
- **全面事件驱动化**：废弃旧 `power-mode/` 每 30 秒轮询与 `battery-charge` 每 5 分钟定时触发（同时删除旧计划任务 `PowerManager-ModeWatch`），改为 Win32 电源/会话通知 + 单一 Waitable Timer，WMI 调用由 PowerShell 改为进程内 `comtypes`。
- **功能定型与合屏**：确定“普通 / 保持运行（定时回落）”双模式、禁用电源键、合盖超时 + 休眠兜底、醒来汇总 Toast 通知与电量每变 5% 记录；合并为单一 `ui` 与快捷方式 `PowerManager.lnk`。

## 2026-11-11（WMI 指令确认与任务触发修复）
- 通过反编译 ACPI `WmiTable.aml` 确认 `03 10`（`SBTT`）为真实阈值控制指令，只读不写 `0x12`（恢复充电）与 `0x15`（充电模式）。
- 结案并否决接入自适应充电算法，保留 EC 自身热保护降档。
- 将 `Battery-ChargeLimit` 计划任务改为 S4U 模式并增加开机与唤醒触发器，以应对系统重启后 EC 阈值复位为 `95–100` 的问题。

## 2026-11-08（基础保护原则确立）
- 确立充电保护约束：不提供永久 100% 充电入口，不设手动恢复阈值按钮，日常基准固定为 `75%–80%`。
