<!--
类别：实现状态。读者：要改代码的人。
装：全局概览、代码地图、不变量、横切关注点（含跨模块改动清单）。
不装：机制细节与字段表（→ docs/design.md）、需求数值（→ docs/requirements.md）、决策论证（→ docs/adr/）。
模块用文字描述并点出关键类型名供搜索，不逐个列文件。
-->

# Architecture

## 全局概览

PowerManager 在一台 MateBook 14 2024 上接管两件事：Windows 睡眠调度（何时关屏、何时睡、睡不下怎么办），以及电池充电阈值。目标与需求见 [docs/requirements.md](docs/requirements.md)。

两件事需要的权限不同，这决定了整体结构：

- **睡眠调度**只需要普通用户权限（`powrprof` 电源设置、Win32 消息、事件日志），由常驻进程 `Agent` 负责。
- **写充电阈值**要调用厂商 WMI 接口，需要管理员权限。它由 `BatteryCharge` 负责：这是一个以 S4U 计划任务方式按需运行的短命进程，做完一次读写就退出。

```text
              普通用户                              │  管理员（计划任务 Battery-ChargeLimit）
                                                    │
  UI ───┐                                           │
        ├── Named Pipe ──►  Agent ── powrprof        │
  pmctl ┘                     │                     │
    │                         │                     │
    └─────────────────────────┴── requests/*.json ──┼──►  BatteryCharge ── WMI ──► EC
                                  + 触发计划任务     │          │
                                                    │          ▼
         UI / pmctl / Agent  ◄──── 读 ──────────────┼──  state.json、日志
```

跨权限只有一条路：普通权限进程把请求写成文件放进 `<DataDir>/requests/`，再触发计划任务；`BatteryCharge` 校验后执行，把结果写进 `state.json`。同权限的 `UI` / `pmctl` 与 `Agent` 之间用 Named Pipe 同步请求。两条通道的契约见 [docs/design.md](docs/design.md)，选型理由见 [ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md)。

四个可执行文件发布到同一目录、共享类库：

| 进程 | 类型 | 身份 | 生命周期 |
|---|---|---|---|
| `PowerManager.Agent.exe` | `WinExe` | 普通用户 | 登录时由计划任务 `PowerManager-Agent` 启动，常驻 |
| `PowerManager.BatteryCharge.exe` | `Exe` | 管理员（S4U） | 开机、登录、唤醒、插拔电或被触发时运行一次即退出 |
| `PowerManager.UI.exe` | `WinExe` | 普通用户 | 用户从快捷方式打开 |
| `pmctl.exe` | `Exe` | 普通用户 | 终端中手动执行 |

## 代码地图

```text
Agent / UI / pmctl ──► Platform ──► Core
BatteryCharge ───────► Platform ──► Core
              └──────► Hardware.MateBook ──► Core
```

### `src/PowerManager.Core/`

纯策略与纯函数决策，不调用任何平台 API，可以全部单测。

- 策略数值：`PowerPolicy`（唯一定义处）；`PolicySnapshot` 是下发给界面的子集；`PolicyText` 把时长格式化为通知文案。
- 决策：`PowerModeCalculator`（模式 → 电源设置与电源模式）、`SleepGuardEngine`（合盖、通勤、主动睡眠与休眠兜底的判定）、`WakeAlarmPlanner`（下一个闹钟时刻、冻结检测）、`WakeSummaryFormatter`（事件日志 → 醒来摘要）、`ChargeDecisionEngine`（冲高与回落）。
- 契约：`AgentIpcRequest` / `AgentIpcResponse`（Named Pipe）、`EcTaskRequest`（请求文件）、`Models/`（状态文件）、`IChargeHardwareAdapter`（硬件层接口）。

### `src/PowerManager.Platform/`

通用 Windows 11 封装，与机型无关。

- Win32：`PowerSchemeInterop`（读写电源设置与电源模式）、`AgentMessageWindow`（接收系统通知）、`WaitableTimerHandle`、`DisplayAndInputInterop`（外接屏与键鼠空闲）、`EventLogReaderInterop`、`ToastNotifierHelper`。
- IPC：`AgentNamedPipeServer` / `AgentNamedPipeClient`、`ScheduledTaskTrigger`。
- 存储：`AppPaths`、`AtomicFileStore`（原子写、请求文件消费）、`RotatingUtcLogger`。

### `src/PowerManager.Hardware.MateBook/`

本机型专用的 WMI / EC 适配。`MateBookWmiCodec` 负责指令帧编解码（纯函数），`MateBookChargeHardwareAdapter` 负责 WMI 调用与写后读回校验。硬件事实见 [wmi-ec-interface.md](docs/reference/wmi-ec-interface.md)；换机型见 [porting-guide.md](docs/guides/porting-guide.md)。

### `src/PowerManager.Agent/`

常驻调度宿主。`AgentController` 是主消息循环，把系统事件、闹钟和管道请求串行处理；`AgentSystemActions` 集中所有副作用（写电源设置、存原始快照、触发计划任务、休眠、通知）；`AgentAlarmScheduler` 把所有未来时间点合并到一个定时器；`WakeSummaryCoordinator` 负责醒来通知的延迟查询与锁屏暂存。Windows 行为边界见 [windows-standby-and-toast.md](docs/reference/windows-standby-and-toast.md)，睡眠方案见 [ADR-0002](docs/adr/0002-modern-standby-fallback.md)。

### `src/PowerManager.BatteryCharge/`

提权硬件任务。`Program` 消费请求文件、读写阈值、执行 `ChargeDecisionEngine` 的决策、写 `state.json` 与日志。不引用 WebView2。

### `src/PowerManager.UI/`

WinForms + WebView2 界面。`MainForm` 是窗口宿主，`WebRpcBridge` 路由前端的 JSON-RPC 调用，`UiBackendService` 汇总状态并转发操作。前端是 `web/` 下的静态文件，由 `EmbeddedWebAssets` 内嵌进程序；没有宿主时 `mock.js` 提供离线数据，可直接用浏览器打开预览。界面设计见 [ui-design.md](docs/ui-design.md)。

### `src/PowerManager.Cli/`

`pmctl`：只做参数解析与转发，不常驻、不提权。

### `scripts/`、`install.ps1`

`publish.ps1` 发布四个程序到 `dist/`；`install.ps1` 注册计划任务、快捷方式与通知标识。`verify_*.py` 是实机与文档校验脚本，刻意不依赖主程序。

## 不变量

**架构不变量**：

| 编号 | 不变量 | 违反后果 | 守护位置 |
|---|---|---|---|
| `A1` | `Core` 不引用任何平台 API 或外部包 | 决策逻辑无法单测 | `PowerManager.Core.csproj` 目标框架为 `net10.0`（非 Windows） |
| `A2` | 只有 `BatteryCharge` 引用 `Hardware.MateBook`；`Agent`、`UI`、`pmctl` 不含任何厂商 WMI / EC 逻辑 | 普通权限进程试图直写 EC；换机型时要改上层 | 项目引用（编译期） |
| `A3` | 普通权限进程不直接读写 EC，只通过请求文件 + 触发计划任务与 `BatteryCharge` 交互 | 绕过提权边界与白名单校验 | `A2` + `ScheduledTaskTrigger` |
| `A4` | 事件驱动：`Agent` 不做全局周期轮询，所有未来时间点由同一个不唤醒电脑的定时器驱动；只有通勤期间的电池温度按需低频采样 | 无效唤醒与常驻开销（[ADR-0004](docs/adr/0004-event-driven-scheduling.md)） | `AgentAlarmScheduler`、`WaitableTimerHandle` |
| `A5` | Windows 电源设置正常情况下只由 `Agent` 修改；唯一例外是 `Agent` 未运行时 `pmctl restore-original` 直接恢复 | 多方写入互相覆盖 | `PowerSchemeInterop` 的调用方仅为 `AgentSystemActions` 与 `Cli` 的恢复分支 |
| `A6` | 用户可感知的策略数值只在 `PowerPolicy` 定义；界面经 `get_state` 的 `policy` 取值，通知经 `PolicyText` 拼写，都不手写数字；需求与设计文档中的数值与它一致 | 界面、通知、文档与实际行为不一致 | `UiPolicyConsistencyTests`、`DocPolicyConsistencyTests` |

**安全约束**（违反会伤电池、让电脑在包里发热，或丢失用户原有设置；确需突破须先改写对应 ADR）：

| 编号 | 约束 | 违反后果 | 守护位置 |
|---|---|---|---|
| `C1` | EC 只写 `03 10`（`SBTT`），其他 WMI 写入指令（`0x12`、`0x15`、`0x18`、`0x04` 等）一律不调用 | 这些指令的固件状态机尚未查明，可能干扰智能充电或温控 | `MateBookWmiCodec` 中写入类构造只有 `BuildSetThresholds`；新增 `BuildInput` 调用须对照 [wmi-ec-interface.md](docs/reference/wmi-ec-interface.md) 确认是读 |
| `C2` | 写阈值后必须读回 `03 11` 校验；不一致记 `MISMATCH` 并以非零退出码结束 | 阈值没生效却被当成已生效，电池长期满充 | `MateBookChargeHardwareAdapter.WriteChargeThresholdsAndVerify`；`BatteryCharge` 退出码 `2` |
| `C3` | `Agent` 不唤醒电脑，也不阻止睡眠：定时器只用 `SetWaitableTimer(fResume: false)`，不调用 `SetThreadExecutionState` / `PowerSetRequest` | 放进包里后被反复唤醒或睡不下去 | `WaitableTimerHandle`；`rg "fResume: true\|SetThreadExecutionState\|PowerSetRequest" src` 应无结果 |
| `C4` | 提权任务只执行通过白名单校验的请求：拒绝重解析点，校验 `kind`、`reason` 与参数边界，不合格即丢弃 | `requests/` 普通用户可写，可借提权任务写入任意阈值或文件 | `AtomicFileStore.ConsumeEcRequests`、`EcTaskRequest.TryValidate`；`SchemaCompatibilityTests` |
| `C5` | 首次修改 Windows 电源设置前必须已保存 `original.json`；已存在时不覆盖 | 停用与卸载时无法恢复用户原来的设置 | `AgentSystemActions.EnsureOriginalSnapshot`（先于 `ApplyPowerScheme`）；`install.ps1 -ImportOriginalFrom` 默认不覆盖 |

关机语义不变见需求 `SLP-09`：代码中不应出现 `ExitWindowsEx` / `InitiateShutdown` 等关机调用。

## 横切关注点

- **时间**：日志与状态文件一律用 UTC（ISO 8601）；界面与通知显示本机时间。
- **数据目录**：运行时状态、请求与日志只放在 `<DataDir>`（生产 `%LOCALAPPDATA%\PowerManager`，开发 `--data-dir ./var`），不进仓库；代码中不写死用户目录。各文件用途见 [design.md](docs/design.md)。
- **实机验证先行**：新增 WMI 指令、更换机型，或改动睡眠、电源键、合盖相关的系统调用之前，先用 `scripts/` 下的独立脚本在实机上确认行为，结论写进 `docs/reference/`。判断是否生效看物理表现（如 `ChargeRate` 归零），不看接口返回码。脚本若临时修改系统设置，须用 `try…finally` 保证退出即恢复。
- **测试分层**：
  - `Core` 纯函数与契约兼容：`dotnet test PowerManager.sln`；
  - Named Pipe 回环与平台封装：同上（`AgentNamedPipeIntegrationTests`、`PlatformStorageAndXmlTests`）；
  - 硬件与系统边界：`verify_wmi_readonly.py`、`verify_power_boundaries.py`（实机）；
  - 界面布局：`verify_ui_layout.py`，加 `PowerManager.UI.exe --dry-run --smoke-test` 与目视检查；
  - 文档引用：`verify_doc_references.py`。
- **CI**：每次推送与 PR 在 `windows-latest` 上运行 `verify_doc_references.py` 与 `dotnet test`（`.github/workflows/ci.yml`）；实机脚本与界面检查不在 CI 中。

### 跨模块改动清单

| 改动 | 需要同时修改 | 检查 |
|---|---|---|
| 改策略数值 | `PowerPolicy`；[requirements.md](docs/requirements.md) 中对应规格；界面要展示的，加进 `PolicySnapshot` 与 `web/mock.js` 的 `policy` 块 | `dotnet test`（`UiPolicyConsistencyTests`、`DocPolicyConsistencyTests`） |
| 改 IPC 或状态文件字段 | `Contracts/IpcContracts.cs` / `Models/` / `UiModels`；[design.md](docs/design.md) 的契约表 | `dotnet test`（`SchemaCompatibilityTests`、`AgentNamedPipeIntegrationTests`） |
| 新增 WMI 指令 | 先写实机只读验证 → [wmi-ec-interface.md](docs/reference/wmi-ec-interface.md) → `MateBookWmiCodec`；复核 `C1` | `verify_wmi_readonly.py` + `dotnet test` |
| 增删需求 | [requirements.md](docs/requirements.md)；代码注释与 design.md 中的需求编号；废弃编号写进"已废弃编号" | `verify_doc_references.py` |
| 改前端样式或布局 | `web/` 下的文件；[ui-design.md](docs/ui-design.md) | `verify_ui_layout.py` + 冒烟测试 + 目视 |
| 改发布或安装方式 | `scripts/publish.ps1` / `install.ps1`；README 的安装与升级 | 实际执行一次发布与安装 |
