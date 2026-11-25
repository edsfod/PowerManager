# Architecture & Code Map

本文档是 `PowerManager` 的**顶层代码地图与改动导航**：用于快速定位源码入口、确认跨进程链路与架构不变量，并按改动任务给出最小阅读清单。具体业务参数与需求追踪见 [docs/design.md](docs/design.md) 与 [docs/requirements.md](docs/requirements.md)。

---

## 1. 项目依赖与架构不变量

```text
PowerManager.Agent         ──┐
PowerManager.UI            ──┼──► PowerManager.Platform ──► PowerManager.Core
PowerManager.Cli (pmctl)   ──┘                                     ▲
PowerManager.BatteryCharge ──────► PowerManager.Platform           │
                           ──────► PowerManager.Hardware.MateBook ─┘
```

**不可破坏的架构不变量**：
1. **硬件隔离**：仅 `PowerManager.BatteryCharge` 引用 `PowerManager.Hardware.MateBook`；上层进程严禁出现 WMI 字节指令或 EC 寄存器假设（[docs/conventions.md](docs/conventions.md)）。
2. **权限边界**：`Agent`、`UI`、`pmctl` 均以普通用户权限运行，不弹 UAC；写 EC 与读温度传感器统一由 S4U 提权任务 `Battery-ChargeLimit`（`PowerManager.BatteryCharge.exe`）执行（[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md)）。
3. **事件驱动与纯函数**：`Agent` 严禁引入周期轮询，所有定时由单一不唤醒的 Waitable Timer 驱动（[ADR-0004](docs/adr/0004-event-driven-migration.md)）；核心决策逻辑留在 `PowerManager.Core/Logic/` 保持无 IO 副作用，可独立单测。
4. **策略红线**：日常 `75–80%` 充电上限仅定义在 `PowerPolicy.cs`，不提供永久 100% 开关，不做自适应充电（[ADR-0003](docs/adr/0003-rejection-of-adaptive-charge.md)）。

---

## 2. 代码地图（Code Map）

| 项目 / 目录 | 职责 | 核心文件 | 对应测试 |
|---|---|---|---|
| `src/PowerManager.Core/` | 纯策略常量、纯函数决策、模型与接口契约（0 外部依赖） | `Policy/PowerPolicy.cs`<br>`Logic/PowerModeCalculator.cs`、`SleepGuardEngine.cs`、`WakeAlarmPlanner.cs`、`WakeSummaryFormatter.cs`、`ChargeDecisionEngine.cs`<br>`Contracts/IpcContracts.cs`、`Abstractions/IHardwareAdapter.cs`、`Models/*` | `PowerModeCalculatorTests.cs`<br>`SleepGuardEngineTests.cs`<br>`WakeAlarmPlannerTests.cs`<br>`WakeSummaryFormatterTests.cs`<br>`ChargeDecisionEngineTests.cs`<br>`SchemaCompatibilityTests.cs` |
| `src/PowerManager.Platform/` | Windows 平台封装、IPC 与持久化 | `Win32/PowerSchemeInterop.cs`、`AgentMessageWindow.cs`、`DisplayAndInputInterop.cs`、`WaitableTimerHandle.cs`、`EventLogReaderInterop.cs`、`ToastNotifier.cs`<br>`Ipc/AgentNamedPipe.cs`、`ScheduledTaskTrigger.cs`<br>`Storage/AppPaths.cs`、`AtomicFileStore.cs`、`RotatingUtcLogger.cs` | `AgentNamedPipeIntegrationTests.cs`<br>`PlatformStorageAndXmlTests.cs` |
| `src/PowerManager.Hardware.MateBook/` | MateBook 14 2024 专用 WMI/EC 硬件适配器 | `MateBookWmiCodec.cs`（指令帧编解码）<br>`MateBookChargeHardwareAdapter.cs`（WMI/COM 调用） | `MateBookWmiCodecTests.cs`<br>实机校验：`scripts/verify_wmi_readonly.py` |
| `src/PowerManager.Agent/` | 常驻调度宿主（`WinExe`，普通用户） | `Program.cs`（启动与参数）<br>`AgentController.cs`（事件循环、IPC 队列与状态流转编排）<br>`AgentSystemActions.cs`（电源方案/模式、快照、EC 触发、休眠与 Toast 副作用）<br>`AgentAlarmScheduler.cs`（闹钟汇总与 WaitableTimer 驱动）<br>`WakeSummaryCoordinator.cs`（离开记录、15s 延迟查询与锁屏暂存补发） | 依赖 `Core` 单测 + `AgentNamedPipeIntegrationTests.cs` |
| `src/PowerManager.BatteryCharge/` | 提权硬件控制任务（`Exe`，S4U 管理员） | `Program.cs`（消费 `requests/`、读/写 EC 阈值、落盘 `state.json` 与日志） | `ChargeDecisionEngineTests.cs` + `MateBookWmiCodecTests.cs` |
| `src/PowerManager.UI/` | WebView2 桌面交互界面（`WinExe`，普通用户） | `MainForm.cs`（WinForms + WebView2 窗口宿主）<br>`WebRpcBridge.cs`（JSON-RPC 10 项桥接路由）<br>`WindowWidthAnimator.cs`（DPI 尺寸与展开动画）<br>`EmbeddedWebAssets.cs`（内嵌静态资源拼装与 DWM 主题）<br>`UiBackendService.cs`、`UiStateFormatter.cs`、`UiModels.cs`<br>`web/`：`index.html`、`styles.css`、`mock.js`、`app.js` | 支持浏览器直接打开 `web/index.html` 离线预览，或 `PowerManager.UI.exe --smoke-test` |
| `src/PowerManager.Cli/` | 终端薄命令行入口（`pmctl.exe`） | `Program.cs`（`status` / `mode` / `boost` / `restore-original`） | — |
| 部署与校验脚本 | 统一发布、计划任务注册与边界验证 | `scripts/publish.ps1`、`install.ps1`<br>`scripts/verify_doc_traceability.py`、`verify_power_boundaries.py`、`verify_wmi_readonly.py` | `dotnet test PowerManager.sln` |

---

## 3. 三条跨进程主链路

1. **模式切换（`UI` / `pmctl` → `Agent`）**
   `UiBackendService` 或 `pmctl` → Named Pipe (`\\.\pipe\PowerManager.Agent`，`AgentNamedPipe.cs`) → `AgentController` 投递至主消息循环 → `PowerModeCalculator` 计算参数 → `PowerSchemeInterop` 写 `powrprof` + 落盘 `<DataDir>/agent/mode.json` → 同步返回 JSON 回执。
2. **合盖 / 过热保护与休眠兜底（`Agent` ↔ `BatteryCharge`）**
   `AgentMessageWindow` 收到合盖/电量/锁屏事件 + `WaitableTimerHandle` 到期 → `SleepGuardEngine` & `WakeAlarmPlanner` 判定动作 →（保持运行用电池时）`ScheduledTaskTrigger` 拉起 `Battery-ChargeLimit` 写温度到 `state.json`，10 秒后 `Agent` 回读 → 触发缩短超时或 `SetSuspendState(Hibernate)` → 醒来后 `EventLogReaderInterop` + `WakeSummaryFormatter` + `ToastNotifier` 发通知。
3. **单次临时冲高（`UI` / `pmctl` → `BatteryCharge`）**
   调用方通过 `AtomicFileStore` 在 `<DataDir>/requests/` 原子写入请求文件 → `ScheduledTaskTrigger` 触发队列化计划任务 `Battery-ChargeLimit` → `BatteryCharge/Program.cs` 校验边界并消费请求 → `ChargeDecisionEngine.Decide()` 决策 → `MateBookChargeHardwareAdapter` 写并回读校验 EC → 更新 `<DataDir>/battery-charge/state.json` 与 `ec.log` / `history.log`。

---

## 4. 按修改任务的最小阅读清单

改动具体模块时，**无需通读 `docs/` 全部文档**，按场景只看对应项即可：

| 我要改什么 | 只需先看哪节文档 | 重点关注源码 | 改完必跑校验 |
|---|---|---|---|
| **桌面 UI / 前端样式 / 桥接方法** | [docs/ui-design.md](docs/ui-design.md)、[docs/design.md](docs/design.md) §四 | `src/PowerManager.UI/`、`Core/Contracts/IpcContracts.cs` | `python scripts/verify_ui_layout.py` + 目视截图 + `dotnet build PowerManager.sln` |
| **睡眠策略 / 合盖休眠兜底 / 醒来通知** | [docs/design.md](docs/design.md) §二、[docs/reference/windows-standby-and-toast.md](docs/reference/windows-standby-and-toast.md) | `Core/Policy/PowerPolicy.cs`、`Core/Logic/`、`Agent/AgentController.cs`、`Platform/Win32/` | `dotnet test PowerManager.sln`（涉及电源边界加跑 `verify_power_boundaries.py`） |
| **充电阈值 / 临时冲高 / WMI 硬件适配** | [docs/design.md](docs/design.md) §三、[docs/reference/wmi-ec-interface.md](docs/reference/wmi-ec-interface.md)、[ADR-0003](docs/adr/0003-rejection-of-adaptive-charge.md) | `Core/Logic/ChargeDecisionEngine.cs`、`Hardware.MateBook/`、`BatteryCharge/Program.cs` | `dotnet test PowerManager.sln` + 实机只读 `verify_wmi_readonly.py` |
| **跨进程 IPC / 状态文件 Schema / `pmctl`** | [docs/design.md](docs/design.md) §四–§五、[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md) | `Core/Contracts/`、`Core/Models/`、`Platform/Ipc/`、`Platform/Storage/`、`Cli/Program.cs` | `dotnet test PowerManager.sln`（重点关注 `AgentNamedPipeIntegrationTests` 与 `SchemaCompatibilityTests`） |
| **构建打包 / 计划任务安装脚本** | [README.md](README.md)、[ADR-0005](docs/adr/0005-choose-csharp-framework-dependent.md)、[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md) | `scripts/publish.ps1`、`install.ps1`、`Directory.Build.props` | `scripts/publish.ps1` 发布核验 |
| **需求增删 / 架构变更 / 文档调整** | [docs/requirements.md](docs/requirements.md)、[docs/conventions.md](docs/conventions.md) §五 | — | `python scripts/verify_doc_traceability.py` |
