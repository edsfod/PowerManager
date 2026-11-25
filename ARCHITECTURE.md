# Architecture & Code Map

本文档是 `PowerManager` 的**顶层代码地图与改动导航**：项目依赖、改动时不可违反的红线、源码入口、跨进程链路、按任务的最小阅读清单，以及工程与文档约定。具体业务参数与需求追踪见 [docs/design.md](docs/design.md) 与 [docs/requirements.md](docs/requirements.md)。

---

## 1. 项目依赖与红线

```text
PowerManager.Agent         ──┐
PowerManager.UI            ──┼──► PowerManager.Platform ──► PowerManager.Core
PowerManager.Cli (pmctl)   ──┘                                     ▲
PowerManager.BatteryCharge ──────► PowerManager.Platform           │
                           ──────► PowerManager.Hardware.MateBook ─┘
```

**红线**：违反任何一条都会伤到电池、让电脑在包里发热，或丢失用户原有设置。改动涉及时必须守住，确需突破须先新增 ADR。代码注释用编号引用（如 `R2`）。

| 编号 | 红线 | 违反后果 | 守护位置 |
|---|---|---|---|
| `R1` | **EC 只写 `03 10`（`SBTT`）**，其他 WMI 指令一律只读（`0x12`、`0x15`、`0x18`、`0x04` 等写入指令不得调用） | 这些指令在 EC 固件内的状态机尚未查明（[wmi-ec-interface.md](docs/reference/wmi-ec-interface.md) §五），可能干扰固件智能充电或温控 | `MateBookWmiCodec.cs`：写入类构造只有 `BuildSetThresholds`；新增 `BuildInput(0x03, …)` / `BuildInput(0x04, …)` 时须逐条对照参考文档确认是读 |
| `R2` | **写阈值后必须读回 `03 11` 校验**，不一致时记 `MISMATCH` 并以非零退出码结束，不得静默继续 | 阈值没生效却被当成已生效，电池长期满充 | `MateBookChargeHardwareAdapter.WriteChargeThresholdsAndVerify`；`BatteryCharge/Program.cs`（退出码 `2`） |
| `R3` | **Agent 不得唤醒电脑，也不得阻止睡眠**：定时器只用 `SetWaitableTimer(fResume: false)`，不调用 `SetThreadExecutionState` / `PowerSetRequest` | 合盖放进包里后被反复唤醒或睡不下去，正是本项目要防止的发热耗电 | `Platform/Win32/WaitableTimerHandle.cs`；`rg "fResume: true\|SetThreadExecutionState\|PowerSetRequest" src` 应无结果 |
| `R4` | **提权任务只信任通过白名单校验的请求**：`<DataDir>/requests/` 普通用户可写，而 `Battery-ChargeLimit` 以最高权限运行，必须拒绝重解析点，校验 `kind`、`reason` 与参数边界，不合格即丢弃 | 普通权限进程借提权任务写入任意阈值或文件 | `AtomicFileStore.ConsumeEcRequests`、`EcTaskRequest.TryValidate`；`SchemaCompatibilityTests`（`EcTaskRequest_`） |
| `R5` | **首次修改 Windows 电源设置前必须已保存 `original.json`，已存在时不得覆盖** | `restore-original` 与卸载无法恢复用户原来的设置 | `AgentSystemActions.EnsureOriginalSnapshot`（启动时先于 `ApplyPowerScheme` 调用）；`install.ps1 -ImportOriginalFrom` 默认不覆盖 |
| `R6` | **不改变关机语义**：不把关机改成休眠或睡眠，不拦截关机流程（`SLP-09`） | 用户以为已关机，实际是休眠或待机，在包里继续耗电发热 | 代码中不应出现 `ExitWindowsEx` / `InitiateShutdown` 等关机调用；电源键动作只设为“不采取任何操作” |

---

## 2. 代码地图（Code Map）

| 项目 / 目录 | 职责 | 核心文件 | 对应测试 |
|---|---|---|---|
| `src/PowerManager.Core/` | 纯策略常量、纯函数决策、模型与接口契约（0 外部依赖） | `Policy/PowerPolicy.cs`（策略常量）、`PolicySnapshot.cs`（下发界面的策略数值）、`PolicyText.cs`（策略时长文案）<br>`Logic/PowerModeCalculator.cs`、`SleepGuardEngine.cs`、`WakeAlarmPlanner.cs`、`WakeSummaryFormatter.cs`、`ChargeDecisionEngine.cs`<br>`Contracts/IpcContracts.cs`、`Abstractions/IHardwareAdapter.cs`、`Models/*` | `PowerModeCalculatorTests.cs`<br>`SleepGuardEngineTests.cs`<br>`WakeAlarmPlannerTests.cs`<br>`WakeSummaryFormatterTests.cs`<br>`ChargeDecisionEngineTests.cs`<br>`SchemaCompatibilityTests.cs`<br>`UiPolicyConsistencyTests.cs` |
| `src/PowerManager.Platform/` | Windows 平台封装、IPC 与持久化 | `Win32/PowerSchemeInterop.cs`、`AgentMessageWindow.cs`、`DisplayAndInputInterop.cs`、`WaitableTimerHandle.cs`、`EventLogReaderInterop.cs`、`ToastNotifier.cs`<br>`Ipc/AgentNamedPipe.cs`、`ScheduledTaskTrigger.cs`<br>`Storage/AppPaths.cs`、`AtomicFileStore.cs`、`RotatingUtcLogger.cs` | `AgentNamedPipeIntegrationTests.cs`<br>`PlatformStorageAndXmlTests.cs` |
| `src/PowerManager.Hardware.MateBook/` | MateBook 14 2024 专用 WMI/EC 硬件适配器 | `MateBookWmiCodec.cs`（指令帧编解码）<br>`MateBookChargeHardwareAdapter.cs`（WMI/COM 调用） | `MateBookWmiCodecTests.cs`<br>实机校验：`scripts/verify_wmi_readonly.py` |
| `src/PowerManager.Agent/` | 常驻调度宿主（`WinExe`，普通用户） | `Program.cs`（启动与参数）<br>`AgentController.cs`（事件循环、IPC 队列与状态流转编排）<br>`AgentSystemActions.cs`（电源方案/模式、快照、EC 触发、休眠与 Toast 副作用）<br>`AgentAlarmScheduler.cs`（闹钟汇总与 WaitableTimer 驱动）<br>`WakeSummaryCoordinator.cs`（离开记录、15s 延迟查询与锁屏暂存补发） | 依赖 `Core` 单测 + `AgentNamedPipeIntegrationTests.cs` |
| `src/PowerManager.BatteryCharge/` | 提权硬件控制任务（`Exe`，S4U 管理员） | `Program.cs`（消费 `requests/`、读/写 EC 阈值、落盘 `state.json` 与日志） | `ChargeDecisionEngineTests.cs` + `MateBookWmiCodecTests.cs` |
| `src/PowerManager.UI/` | WebView2 桌面交互界面（`WinExe`，普通用户） | `MainForm.cs`（WinForms + WebView2 窗口宿主）<br>`WebRpcBridge.cs`（JSON-RPC 桥接路由，方法表见 design.md §四）<br>`WindowWidthAnimator.cs`（DPI 尺寸与展开动画）<br>`EmbeddedWebAssets.cs`（内嵌静态资源拼装与 DWM 主题）<br>`UiBackendService.cs`、`UiStateFormatter.cs`、`UiModels.cs`<br>`web/`：`index.html`、`styles.css`、`mock.js`、`app.js` | 支持浏览器直接打开 `web/index.html` 离线预览，或 `PowerManager.UI.exe --smoke-test` |
| `src/PowerManager.Cli/` | 终端薄命令行入口（`pmctl.exe`） | `Program.cs`（`status` / `mode` / `boost` / `restore-original` / `paths`） | — |
| 部署与校验脚本 | 统一发布、计划任务注册与边界验证 | `scripts/publish.ps1`、`install.ps1`<br>`scripts/verify_doc_traceability.py`、`verify_power_boundaries.py`、`verify_wmi_readonly.py`、`verify_ui_layout.py` | `dotnet test PowerManager.sln` |

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
| **睡眠策略 / 合盖休眠兜底 / 醒来通知** | §1 红线 `R3`、`R5`、`R6`；[docs/design.md](docs/design.md) §二、[docs/reference/windows-standby-and-toast.md](docs/reference/windows-standby-and-toast.md) | `Core/Policy/PowerPolicy.cs`、`Core/Logic/`、`Agent/AgentController.cs`、`Platform/Win32/` | `dotnet test PowerManager.sln`（涉及电源边界加跑 `verify_power_boundaries.py`） |
| **充电阈值 / 临时冲高 / WMI 硬件适配** | §1 红线 `R1`、`R2`；[docs/design.md](docs/design.md) §三、[docs/reference/wmi-ec-interface.md](docs/reference/wmi-ec-interface.md)、[ADR-0003](docs/adr/0003-rejection-of-adaptive-charge.md) | `Core/Logic/ChargeDecisionEngine.cs`、`Hardware.MateBook/`、`BatteryCharge/Program.cs` | `dotnet test PowerManager.sln` + 实机只读 `verify_wmi_readonly.py` |
| **跨进程 IPC / 状态文件 Schema / `pmctl`** | §1 红线 `R4`；[docs/design.md](docs/design.md) §四–§五、[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md) | `Core/Contracts/`、`Core/Models/`、`Platform/Ipc/`、`Platform/Storage/`、`Cli/Program.cs` | `dotnet test PowerManager.sln`（重点关注 `AgentNamedPipeIntegrationTests` 与 `SchemaCompatibilityTests`） |
| **构建打包 / 计划任务安装脚本** | [README.md](README.md)、[ADR-0005](docs/adr/0005-choose-csharp-framework-dependent.md)、[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md) | `scripts/publish.ps1`、`install.ps1`、`Directory.Build.props` | `scripts/publish.ps1` 发布核验 |
| **需求增删 / 架构变更 / 文档调整** | [docs/requirements.md](docs/requirements.md)、本文 §5 | — | `python scripts/verify_doc_traceability.py` |

---

## 5. 工程与文档约定

**工程约定**：
- **改硬件或固件相关调用前先实机验证**：新增 WMI 指令、更换机型，或改动与睡眠、电源键、合盖行为边界相关的调用之前，先用 `scripts/` 下的独立脚本在实机上确认行为，并把结论写进 `docs/reference/`。判断是否生效看物理表现（例如 `ChargeRate` 归零），不看接口返回码。验证脚本刻意不依赖主程序，所以用 Python 编写（也沿袭自 v1）；脚本若临时修改系统设置，须用 `try…finally` 保证退出即恢复。
- **时间**：日志与状态文件一律用 UTC（ISO 8601）；界面与通知显示本机时间。
- **路径与数据**：代码中不写死用户目录；运行时状态与日志只放在 `<DataDir>`（开发期 `var/`），不提交进仓库。

**文档约定**：
- **分工**：[requirements.md](docs/requirements.md) 写“做什么”，是文档中策略数值的权威来源；[design.md](docs/design.md) 写“怎么做”，包括 IPC 与状态文件字段表；[ui-design.md](docs/ui-design.md) 写界面；`docs/adr/` 写“为什么这样选”；`docs/reference/` 只写硬件和系统的客观事实；本文写代码地图、红线与约定；[CHANGELOG.md](CHANGELOG.md) 记历史，已发布条目只更正事实错误、不追改内容。
- **防止漂移**：策略数值以 [PowerPolicy.cs](src/PowerManager.Core/Policy/PowerPolicy.cs) 为准：界面文案的数字经 `get_state` 的 `policy` 下发，通知与备注用 `PolicyText` 拼写，两者都不手写数字（前端由 `UiPolicyConsistencyTests` 守护）；其他文档优先引用需求编号而非重抄数值；不写“共 N 项”“N/N 通过”这类会随代码变化的计数；契约字段改名或新增时，同步更新 design.md 字段表与 `SchemaCompatibilityTests`；增删需求后运行 `python scripts/verify_doc_traceability.py`。
- **隐私**：入库文档不写个人路径、与本项目无关的软件使用记录或作息时间。排查记录放在本地 `docs/investigations/`（已列入 `.gitignore`），可长期沿用的事实提炼进 `docs/reference/` 后再引用。
