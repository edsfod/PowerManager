# 系统设计（Design）

本文档描述 `PowerManager` 当前生效的**软件架构、模块契约、状态流转与持久化机制**（需求依据见 [requirements.md](requirements.md)，架构决策依据见 `docs/adr/`）。

---

## 一、 架构分层与运行实体

系统遵循“上层通用 Windows 调度”与“底层硬件适配器”分离的原则（[ADR-0001](adr/0001-scope-and-single-model.md)、[ADR-0006](adr/0006-csharp-architecture-ipc-and-data-dir.md)），采用单一 `.NET 10` 解决方案、三层共享类库（`PowerManager.Core`、`PowerManager.Platform`、`PowerManager.Hardware.MateBook`，配 `PowerManager.Tests` 单元测试）与四个独立可执行文件（发布至同一目录共享底层 DLL，满足 `NFR-02`）：

| 运行实体 | 可执行文件与子系统 | 运行身份 | 生命周期与触发方式 | 核心职责 |
|---|---|---|---|---|
| **常驻调度（`agent`）** | `PowerManager.Agent.exe`（`WinExe`） | 普通用户（不提权） | 计划任务 `PowerManager-Agent` 登录时启动（无超时限制 `PT0S`、多实例 `IgnoreNew`、允许电池运行、失败每 1 分钟重启最多 5 次）；纯事件驱动阻塞等待 | 监听系统电源/合盖/屏幕/电量/锁屏通知；按模式修改超时与电源模式；维护单一 Waitable Timer；执行休眠兜底与醒来通知；提供 Named Pipe 控制接口；按需拉起 EC 任务（10 秒后读取温度结果） |
| **硬件控制任务（`battery-charge`）** | `PowerManager.BatteryCharge.exe`（`Exe`） | 管理员（S4U 最高权限） | 计划任务 `Battery-ChargeLimit`（执行限时 `PT2M`、允许电池运行、多实例 `Queue`），在开机、登录、唤醒（`Power-Troubleshooter 1`）、插拔电（`Kernel-Power 105`）及被显式触发时运行一次即退出 | 消费 `<DataDir>/requests/` 请求；调用硬件适配器读写充电阈值与温度传感器；执行冲高与回落决策；写入 `state.json` 与历史记录 |
| **交互界面（`ui`）** | `PowerManager.UI.exe`（`WinExe`） | 普通用户 | 由桌面/开始菜单快捷方式 `PowerManager.lnk` 按需启动（WebView2 渲染前端） | 展示当前模式、充电阈值与温度状态；通过 Named Pipe 向 `agent` 切换模式，通过请求文件触发 `Battery-ChargeLimit` 发起临时冲高 |
| **命令行工具（`pmctl`）** | `pmctl.exe`（`Exe`） | 普通用户（不提权，不直接访问 EC；需在管理员终端直写 EC 时改用 `PowerManager.BatteryCharge.exe --status` / `--boost`） | 终端按需手动执行 | 薄控制台入口，统一承接 `status`、`mode`、`boost`、`restore-original`、`paths` 终端查看与控制命令，避免 `WinExe` 后台进程在终端输出错位 |

**依赖方向约束**：编译期强制隔离——`PowerManager.Agent`、`PowerManager.UI`、`pmctl` 仅引用 `Core` 与 `Platform`，严禁引用 `PowerManager.Hardware.MateBook` 或包含厂商专有 WMI 字节/EC 寄存器逻辑；`PowerManager.BatteryCharge` 引用 `Core`、`Platform` 与 `Hardware.MateBook`（是唯一引用硬件适配层的项目），不携带 WebView2 依赖。普通权限进程仅通过 `<DataDir>/requests/` 原子 JSON 请求文件 + 触发 `Battery-ChargeLimit` 与状态文件 `state.json` 和硬件任务交互。

---

## 二、 通用 Windows 调度层（可跨机器复用）

### 1. 事件监听与单一闹钟机制（[ADR-0004](adr/0004-event-driven-migration.md)）
`agent` 通过 Win32 消息循环接收以下系统推送事件，不进行周期轮询（满足 `NFR-01`）：
- **合盖 / 开盖**、**显示器开 / 关**：判断离开与回来；结合 `QueryDisplayConfig` 枚举是否存在内置屏以外的活动显示器（[windows-standby-and-toast.md](reference/windows-standby-and-toast.md)）。
- **插电 / 拔电**、**电量每变 1%**：触发电源模式切换（`PWR-01`、`PWR-02`）；每逢 5% 倍数时唤醒 EC 任务记录历史（`CHG-03`）；插拔电及目标电量达成时驱动冲高计时与通勤温度检查启停。
- **冻结 / 唤醒检测**：现代待机（S0ix）下桌面窗口不依赖传统 S3 的挂起/恢复广播，而是通过 Waitable Timer 触发时间晚于目标墙上时间 **> 30 秒**来判定进程刚经历冻结/睡眠（`frozen`），唤醒后按墙上时间重算剩余闹钟并检查是否属于合盖状态下意外唤醒。
- **会话锁屏 / 解锁（`WTSRegisterSessionNotification`）**：用于在解锁后再弹出醒来 Toast 通知。
- **UI 切换请求**：接收界面发起的模式切换请求并应用新模式（`SLP-01`、`SLP-02`）；“保持运行”到期自动切回普通模式时同步弹出 Toast 通知提示。

所有定时任务（普通合盖 1 分钟、保持运行到期、冲高保持满、冲高最长兜底、拔电 10 分钟回落、通勤每 3 分钟查温度、主动睡眠每 1 分钟检查与 2 分钟休眠兜底）统一由**单一不唤醒电脑的 Waitable Timer** 驱动，设至最早到期时刻，系统冻结醒来后按墙上时间重算；若定时器触发晚于目标墙上时间 > 30 秒，即判定系统刚经历冻结/睡眠。

### 2. 双模式策略与系统 API 契约

| 配置项 | 普通模式 | 保持运行模式（1–24 小时，快捷档位 2/4/8/12/24，默认 12 小时） |
|---|---|---|
| 插电关屏 / 睡眠 | 15 分钟 / 1 小时（`SLP-03`） | 15 分钟 / 从不（`SLP-04`） |
| 用电池关屏 / 睡眠 | 5 分钟 / 15 分钟（`SLP-03`） | 5 分钟 / 30 分钟；每 3 分钟查温度 ≥ 40 °C 提前睡（`SLP-05`） |
| Windows 合盖 / 电源键动作 | 均设为“不采取任何操作”（`SLP-06`、`SLP-08`、`SLP-09`） | 同左（`SLP-05`、`SLP-08`、`SLP-09`） |
| 插电电源模式 | `Best Performance`（`PWR-01`） | 同左 |
| 用电池电源模式 | ≥ 50% `Balanced`，< 50% `Best Power Efficiency`（`PWR-02`） | 同左 |

- **设置读写**：通过 `powrprof.dll`（`PowerWriteACValueIndex` / `PowerWriteDCValueIndex` 写入后必须调用 `PowerSetActiveScheme` 刷新生效）直接修改超时设置，通过 `PowerSetUserConfiguredAC/DCPowerMode` 切换电源模式，均无需管理员权限、不拉起外部进程。闲置关屏仅关闭显示器不冻结后台（`SLP-07`）。
- **合盖、过热与兜底休眠（[ADR-0002](adr/0002-modern-standby-fallback.md)）**：
  - 普通模式合盖且无外接活动显示器 1 分钟后（或“保持运行”到期时正合盖），将关屏与睡眠超时临时缩短至 **10 秒关屏 / 15 秒睡眠**以诱发现代待机；开盖或唤醒后恢复原超时（`SLP-06`）。
  - 保持运行电池通勤模式下：系统闲置超时为 30 分钟，若 `idle ≥ 32 分钟`（留 2 分钟余量）仍未睡下则由常驻接管主动睡眠；同时每 3 分钟拉起 EC 任务并在 10 秒后读取 `state.json` 的电池温度（若 `updated_at_utc` 距今超过 120 秒则视为过期忽略），达到 `40 °C`（阈值依据：历史电池 35 W / 插电 45 W 重负载实测峰值 `38.7 °C`）且**非“开盖且 5 分钟内有键鼠操作”**时提前触发睡眠（`SLP-05`）。
  - 主动睡眠期间每 1 分钟检查一次（仅系统醒着时运行）：若检测到**开盖且 60 秒内有键鼠操作**，立即中止本次主动睡眠并恢复原超时；若持续未入睡满 2 分钟（`SLP-10`），或合盖状态下经定时器 `frozen` 检测到意外唤醒后 2 分钟仍醒着且非“保持运行 + 插电”（`SLP-11`），先在当前进程令牌启用 `SeShutdownPrivilege`，再调用 `SetSuspendState(Hibernate)` 强制休眠（若休眠被拒绝则重置计时于下一轮重试）；同时保留 Windows 原生的低电量 `2%` 自动休眠兜底设置不作改动。

### 3. 醒来汇总通知（`NTF-01`）
离开超过 1 分钟且开盖亮屏后，后台线程延迟 15 秒通过 `wevtapi` 查询 `[t_off - 5s, t_on + 15s]` 窗口内的事件日志（`Kernel-Power` 506/507/42 与 `Power-Troubleshooter` 1），按以下规则拼装摘要并写日志：
- **状态判定优先级**：有 `Power-Troubleshooter` 1（`TargetState=5`）休眠记录即计为“休眠”；否则 `507` 累计真实睡眠（`SleepEntered=true`）≥ 60 秒计为“睡眠”；其余计为“醒着”。
- **原因与附加行**：`506` 排除 `Reason=20/25`，`507` 排除 `Reason=20`（不计为短暂唤醒）；若常驻在离开期间记录了主动动作备注（如“合盖 1 分钟”），首条备注覆盖入睡原因，后续备注附在第二行；当处于睡眠/休眠状态且期间累计醒着 ≥ 120 秒时附加显示“醒着共 X 分钟”。
- 若处于锁屏状态则暂存至解锁后，通过 .NET 原生 WinRT Toast API 发送通知：统一使用 `AppUserModelID = "PowerManager"`、`Group = "PowerManager"`，按类别设置 `Tag`（醒来汇总 `wake`、保持运行到期 `mode`）以覆盖旧通知防堆积；XML 强制静音（`<audio silent="true"/>`）、标题单行（`hint-maxLines="1"`），并在程序目录存在 `icon.png` 时附带圆形图标（`appLogoOverride`）。

---

## 三、 本机硬件适配层（MateBook 14 2024 专用）

- **抽象接口边界**：硬件适配层仅向上层暴露语义化读取/控制能力（读写启停充电阈值、读取电池 NTC 温度及传感器/功耗状态），上层不感知具体 WMI 指令。
- **WMI/COM 实现**：通过进程内 WMI/COM（`System.Management` / COM，见 [ADR-0005](adr/0005-choose-csharp-framework-dependent.md)）调用 `root\wmi:OemWMIMethod.OemWMIfun` 执行 `03 10`（写阈值）、`03 11`（读阈值）、`02 0C 15`（电池 NTC 温度）等指令（完整字节契约见 [wmi-ec-interface.md](reference/wmi-ec-interface.md)）；读取 `BatteryStatus` 不提权，写阈值由提权计划任务执行。
- **阈值与冲高决策（[ADR-0003](adr/0003-rejection-of-adaptive-charge.md)）**：日常基准固定为常量 `75%–80%`（不暴露永久修改入口，`CHG-01`）。每次写入前先读 `03 11`，仅在读数与目标不一致或读数异常时执行 `03 10` 写入，写后必须重新读 `03 11` 校验读回一致并记入 `ec.log`（读回不一致须记 `MISMATCH` 并报错退出）。临时冲高期间写为 `(上限-5)%–上限%`；其中 `reached_target` 标志一旦达标即不可逆，而 `full_since` 为**连续**达标插电计时（插电期间电量跌破目标或拔电即清零重计）；满足“达标且插电连续保持满设定分钟”、“达标后拔电”、“未达标拔电满 10 分钟”或“超过最长兜底小时数”任一条件即自动写回 `75%–80%`（`CHG-02`）。
- **重启复位应对**：因重启后 EC 阈值会复位为 `95–100`，`Battery-ChargeLimit` 任务采用 **S4U** 登录方式（无需用户登录即可运行）并绑定开机与唤醒触发器（`CHG-01`）。

---

## 四、 交互界面（`ui`）、命令行（`pmctl`）与跨进程通信

本节定义 `ui` 与 `pmctl` 的**进程边界与跨进程通信契约**；前端信息架构、窗口尺寸策略、草稿状态机与布局验收门禁见 [ui-design.md](ui-design.md)：

- **界面宿主与标识**：`PowerManager.UI.exe` 通过 `Microsoft.Web.WebView2` 加载内嵌静态前端（用户数据目录隔离于 `<DataDir>/ui/webview2/`）；启动时设置 `AppUserModelID = "PowerManager"` 并以按管道名隔离的单实例 `Mutex`（`Local\PowerManager.UI.<pipeName>`）防止重复多开（重复启动激活已有窗口）；支持 `--debug`、`--data-dir`、`--pipe-name`、`--dry-run` 与 `--smoke-test` 参数。
- **前端桥接契约**：宿主与前端通过 WebView2 JSON-RPC 通信（前端 `{ id, method, params }` ↔ 宿主 `{ id, result, error }`），宿主在窗口激活与窗口态变化时分别推送 `{ event: "focus" }` 与 `{ event: "window_state", maximized }`。路由实现见 `WebRpcBridge.cs`，方法如下（除 `get_state` 外均返回 `UiActionResult { ok, msg? }`）：

  | 方法 | 参数 | 说明 |
  |---|---|---|
  | `get_state` | — | 返回 `UiStateView`（顶层字段：`agent`、`dry_run`、`forced_sleep`、`rules_expanded`、`data_dir`、`mode`、`power_mode`、`battery`、`boost`、`boost_prefs`、`policy`；子结构定义见 `UiModels.cs`，`policy` 为界面用到的策略数值，定义见 `Core/Policy/PolicySnapshot.cs`） |
  | `set_mode` | `mode`（`"normal"` / `"run"`）、`hours`（`0` 表示直到手动改回，或 `1–24`） | 经 Named Pipe 转发为 `SetMode` |
  | `boost` | `target`、`hold`（分钟）、`maxh`（小时） | 写入冲高请求文件并触发 `Battery-ChargeLimit` |
  | `refresh_ec` | — | 写入 `reason = "ui-refresh"` 的触发请求并触发 `Battery-ChargeLimit` |
  | `open_data_dir` / `start_agent` | — | 打开 `<DataDir>`；拉起计划任务 `PowerManager-Agent` |
  | `set_expanded` / `set_content_height` | `expanded` / `height` | 规则面板展开状态；前端上报内容高度供宿主贴合窗口 |
  | `close_window` / `smoke_ready` | — | 关闭窗口；`--smoke-test` 下前端就绪信号 |

- **状态读取**：界面与 `pmctl` 只读展示 `<DataDir>/agent/mode.json`、`<DataDir>/battery-charge/state.json`，以及非提权的当前电源模式（`powrprof`）与标准 `BatteryStatus`；界面用户偏好保存于 `<DataDir>/ui/prefs.json`（字段见 §五）。
- **跨进程通信（IPC）契约（[ADR-0006](adr/0006-csharp-architecture-ipc-and-data-dir.md)）**：界面与 `pmctl` 以普通权限运行，不弹出 UAC 确认框；正常情况下系统电源设置只由 `agent` 修改。唯一例外是 `pmctl restore-original` 在 `agent` 未运行（管道不可达）时，直接按 `original.json` 写回 Windows 电源设置。
  - **`ui / pmctl ↔ agent`（同步 Named Pipe）**：`agent` 监听仅限当前用户 SID 访问的 Named Pipe（`\\.\pipe\PowerManager.Agent`，可用 `--pipe-name` 覆盖）。每次连接只交换一轮：客户端写一行 UTF-8 JSON 请求，`agent` 投递至主消息循环执行后写回一行 JSON 响应。契约定义见 `Core/Contracts/IpcContracts.cs`：

    | 方向 | 字段 | 取值 |
    |---|---|---|
    | 请求 | `command` | `"Ping"` / `"GetStatus"` / `"SetMode"` / `"RestoreOriginal"` |
    | 请求 | `mode` | 仅 `SetMode`：`"Normal"` / `"KeepRunning"`（注意与 `mode.json` 的 `normal` / `keep_running` 写法不同） |
    | 请求 | `duration_hours` | 仅 `SetMode` + `KeepRunning`：`0` 或 `1–24`，缺省按默认时长 |
    | 响应 | `ok`、`message` | 是否成功；人类可读结果或失败原因 |
    | 响应 | `mode_state` | 当前 `mode.json` 内容（字段见 §五） |
    | 响应 | `forced_sleep_active`、`data_dir` | 是否处于主动睡眠；`agent` 实际使用的数据目录 |

    `RestoreOriginal` 会写回 `original.json` 并令 `agent` 进程退出，但**不会**禁用计划任务 `PowerManager-Agent`；下次登录时 `agent` 重新启动并重新应用当前模式的电源设置。
  - **`agent / ui / pmctl → Battery-ChargeLimit`（原子请求文件 + 计划任务队列）**：调用方在 `<DataDir>/requests/` 目录先写临时文件再原子重命名为 `<utc-ticks:D20>-<guid>.json`，随后触发计划任务 `Battery-ChargeLimit`（由 `MultipleInstances=Queue` 保证串行执行）。请求字段：

    | 字段 | 取值 |
    |---|---|
    | `kind` | `"Trigger"`（仅刷新/记录）或 `"Boost"`（发起临时冲高） |
    | `reason` | 非空，≤ 120 字符（如 `ui-refresh`、`电量 75%`、`通勤查温`、`pmctl boost 95%`） |
    | `boost` | `kind = "Boost"` 时必填，`"Trigger"` 时必须为空：`{ target_stop, hold_minutes, max_hours }`，边界取自 `PowerPolicy`（与 `CHG-02` 一致） |
    | `created_at_utc` | 请求创建时间（ISO 8601） |

    提权任务按文件名顺序消费请求：强制校验白名单边界，拒绝符号链接/重解析点，校验失败的请求记入 `ec.log` 后丢弃；无论成败，消费完毕即删除请求文件。同一批次存在多条冲高请求时以最后一条为准。

---

## 五、 状态持久化与安全回滚

生产环境运行时状态、请求与日志统一存放在 **`%LOCALAPPDATA%\PowerManager`**（记作 `<DataDir>`，不进入 Git）；`install.ps1` 注册计划任务时将展开后的绝对路径通过 `--data-dir` 显式写入任务参数（避免开机未登录阶段 S4U 任务解析用户环境变量失败），开发与验证阶段通过 `--data-dir ./var` 隔离。

| 文件（相对于 `<DataDir>`） | 类型 | 用途与回滚机制 |
|---|---|---|
| `agent/original.json` | 持久 | 首次改动电源设置前保存的 Windows 原始超时与动作快照（Schema 与 v1 兼容，支持切换时只读导入），供 `pmctl restore-original` 一键恢复（同时令 `agent` 退出，但不禁用其计划任务） |
| `agent/mode.json` | 持久 | 当前模式（普通 / 保持运行）与保持运行到期时间戳 |
| `agent/agent.log` / `error.log` | 日志 | 常驻事件流水（含醒来通知内容，UTC，超 1 MB 轮换）与异常堆栈 |
| `battery-charge/state.json` | 缓存 | 最新 EC 读数、冲高目标与保持计时状态 |
| `battery-charge/ec.log` / `history.log` / `error.log` | 日志 | EC 阈值改写记录、每次运行的电量/功率/温度快照（UTC，`CHG-03`；`history.log` 超 1 MB 轮换 `.1`）与异常堆栈 |
| `requests/*.json` | 瞬态 | 普通权限进程投递给 `Battery-ChargeLimit` 的原子 JSON 触发/冲高请求文件，消费后删除 |
| `ui/prefs.json` | 持久 | 界面用户偏好（保持运行时长、冲高默认参数、规则面板展开状态） |
| `ui/webview2/` | 缓存 | WebView2 运行时用户数据目录（与可执行目录隔离） |

**状态文件字段**（JSON，时间戳均为 UTC ISO 8601，读取时字段名大小写不敏感；类型定义见 `Core/Models/` 与 `UI/UiModels.cs`，向后兼容由 `SchemaCompatibilityTests` 守护）：

| 文件 | 字段 |
|---|---|
| `agent/original.json` | `ac` / `dc`：各含 `display`、`sleep`（秒，`0` 为从不）、`lid`、`button`（Windows 电源动作枚举值）；`saved_at_utc` |
| `agent/mode.json` | `mode`（`"normal"` / `"keep_running"`）；`run_until_utc`（保持运行到期时刻，`null` 表示直到手动改回或普通模式）；`changed_at_utc` |
| `battery-charge/state.json` | `updated_at_utc`；`ec`（`hw_ok`、`hw_start`、`hw_stop`、`battery_c`、`system_w`、`charge_ma`、`sensors_c`、`hottest_c` 及原始字段 `charge_mode_raw`、`resume_flag_raw`，读取失败的项为 `null`）；`os_power`（`ac_online`、`percent`）；`boost`（`active`、`target_start`、`target_stop`、`full_hold_minutes`、`max_total_hours`、`started_at_utc`、`deadline_utc`、`full_since_utc`、`reached_target`、`unplugged_since_utc`、`reverted_at_utc`、`last_reason`）；`target`（`[启充, 停充]`）；`win`（标准 `BatteryStatus` 快照，可为 `null`）；`run_reason` |
| `ui/prefs.json` | `hours`、`boost_target`、`boost_hold`、`boost_maxh`、`rules_expanded` |

**失败语义**：`Battery-ChargeLimit` 写阈值后读回不一致时，在 `ec.log` 记 `MISMATCH`、把堆栈写入 `error.log`，并以退出码 `2` 结束，**不更新** `state.json`；其他异常以退出码 `1` 结束。因此界面上"EC 上次更新时间"停止前进，是硬件任务失败的主要外部信号。

**回滚边界**：Windows 电源设置只在用户主动停用（`pmctl restore-original` 或卸载）时按 `original.json` 恢复，以下情况刻意不回滚：
- **`agent` 异常退出**：已写入的模式设置保持不变，作为兜底，等计划任务重启 `agent`（失败后每 1 分钟重试，最多 5 次）。`agent` 启动时会按当前模式重写一次设置，因此若崩溃发生在主动睡眠期间，临时的 10 秒关屏 / 15 秒睡眠超时会一直保留到 `agent` 重新启动。
- **EC 阈值**：重启后 EC 会复位为 `95–100`，而目标始终是锁定 `75–80`，所以不保存也不恢复“原始阈值”，由开机、唤醒等触发器重新写入。
- **验证脚本**：`scripts/` 下的脚本若临时修改系统设置，须用 `try…finally` 在退出时恢复，与主程序的回滚机制无关。

---

## 六、 可测试性与纯函数边界

所有不涉及 Win32/WMI 副作用的核心决策必须实现为纯函数并配独立单元测试：
- 模式到系统超时/电源模式参数的换算、闹钟到期时刻计算、醒来事件日志解析与摘要拼装；
- 冲高开始、达标保持计时与回落条件判定（`decide()`）。

---

## 七、 需求追踪矩阵（Traceability Matrix）

可通过 [verify_doc_traceability.py](../scripts/verify_doc_traceability.py) 自动校验本矩阵与 [requirements.md](requirements.md) 的**编号集合**完全一致（脚本不校验各行内容是否与代码相符）。“验证方式”列中的测试均位于 `tests/PowerManager.Tests/`，写作 `测试类（方法名前缀）`：

| 需求编号 | 设计实现机制 | 归属模块 | 验证方式 |
|---|---|---|---|
| `SLP-01` | UI / `pmctl` 经同步 Named Pipe 向常驻发起模式切换，无自动实验检测 | `ui` / `agent` | `AgentNamedPipeIntegrationTests` + 手动切换核验 |
| `SLP-02` | `mode.json` 持久化到期时间 + Waitable Timer 到点回落普通模式并发 Toast | `agent` | `PowerModeCalculatorTests`（`EvaluateModeExpiry_`、`IsValidKeepRunningDuration_`）+ `pmctl status` |
| `SLP-03` | 普通模式通过 `powrprof.dll` 写入插电/电池关屏与睡眠超时 | `agent` | `PowerModeCalculatorTests`（`GetTargetPowerScheme_NormalMode_`）+ `pmctl status` |
| `SLP-04` | 保持运行插电设置 15 分钟关屏、睡眠设为从不 | `agent` | `PowerModeCalculatorTests`（`GetTargetPowerScheme_KeepRunningMode_`）+ `pmctl status` |
| `SLP-05` | 保持运行电池合盖不采取操作、30 分钟睡眠、每 3 分钟读温度 ≥ 40 °C 触发睡眠 | `agent` / `battery-charge` | `SleepGuardEngineTests`（`EvaluateCommuteTemperature_`、`ShouldTakeoverCommuteIdle_`）+ 实机温度触发 |
| `SLP-06` | 合盖且 `QueryDisplayConfig` 无外接活动屏 1 分钟后临时缩短超时 | `agent` | `SleepGuardEngineTests`（`ShouldTriggerNormalLidCloseSleep_`）、`PowerModeCalculatorTests`（`GetTargetPowerScheme_ForcedSleep_`）、`PlatformStorageAndXmlTests`（`DisplayAndInputInterop_`）+ 实机合盖 |
| `SLP-07` | 闲置关屏超时独立于睡眠超时（现代待机仅熄屏不冻结后台） | `agent` | `verify_power_boundaries.py heartbeat` |
| `SLP-08` | 电源键系统动作设为“不采取任何操作” | `agent` | `PowerModeCalculatorTests`（`GetTargetPowerScheme_NormalMode_`）+ `pmctl status` + [verify_power_boundaries.py](../scripts/verify_power_boundaries.py) |
| `SLP-09` | 不劫持系统关机流程，保留原生关机语义 | `agent` | 实机开始菜单关机核验 |
| `SLP-10` | 主动触发睡眠 2 分钟后仍唤醒则调用 `SetSuspendState(Hibernate)` | `agent` | `SleepGuardEngineTests`（`EvaluateForcedSleepTick_`、`ResetAfterHibernateRejected_`）+ 高负载合盖实测 |
| `SLP-11` | Waitable Timer 迟到 > 30 秒判定冻结醒来，若合盖且非“保持运行+插电”，2 分钟后重睡或休眠 | `agent` | `WakeAlarmPlannerTests`（`IsTimerFrozen_`）、`SleepGuardEngineTests`（`ShouldTriggerUnexpectedWakeFallback_`）+ 实机唤醒核验 |
| `PWR-01` | 交流电事件触发 `PowerSetUserConfiguredACPowerMode` 为 `Best Performance` | `agent` | `PowerModeCalculatorTests`（`GetWindowsPowerMode_`）+ `pmctl status` |
| `PWR-02` | 电池电量变化通知跨过 50% 时调用 `PowerSetUserConfiguredDCPowerMode` | `agent` | `PowerModeCalculatorTests`（`GetWindowsPowerMode_`）+ 电量阈值核验 |
| `CHG-01` | `03 10` 写入 `75–80%`，S4U 计划任务于开机/登录/唤醒/插拔电时恢复 | `battery-charge` | `ChargeDecisionEngineTests`（`Decide_WhenInactive_`）、`MateBookWmiCodecTests` + [verify_wmi_readonly.py](../scripts/verify_wmi_readonly.py) |
| `CHG-02` | 冲高写入 `(上限-5)%–上限%`，Waitable Timer 与插拔电触发 4 条件回落 | `battery-charge` / `agent` | `ChargeDecisionEngineTests`、`SchemaCompatibilityTests`（`EcTaskRequest_`）+ `pmctl status` |
| `CHG-03` | 电量每变 5%、插拔电、唤醒及写阈值时追加 `history.log` 与 `ec.log` | `battery-charge` / `agent` | `PowerModeCalculatorTests`（`ShouldTriggerBatteryHistory_`）、`PlatformStorageAndXmlTests`（`RotatingUtcLogger_`）+ 检查日志 |
| `NTF-01` | 开盖亮屏延后 15 秒查 `wevtapi`，解锁后通过 .NET 原生 WinRT Toast API 发通知 | `agent` | `WakeSummaryFormatterTests`、`PlatformStorageAndXmlTests`（`EventLogReaderInterop_`、`ToastNotifierHelper_`）+ 合盖 >1 分钟开盖实测 |
| `NFR-01` | 常驻阻塞等待 Win32 事件 + 单一 Waitable Timer；EC 任务去定时化 | `agent` / `battery-charge` | `WakeAlarmPlannerTests`（`ComputeNextAlarm_`）+ 计划任务触发器检查 |
| `NFR-02` | `ui` 整合模式切换与冲高控制，桌面与开始菜单统一 `PowerManager.lnk` | `ui` / `install.ps1` | 快捷方式与界面操作核验 |
| `NFR-03` | 前端草稿编辑状态机（`runDraft` / `pick`）显式确认提交，未确认取消或关窗零 IPC | `ui` | 前端交互核验（无自动化测试）+ [ui-design.md](ui-design.md) §四 |
| `NFR-04` | 单实例 `Mutex`、DWM 深浅色热同步、`ResizeObserver` 内容贴合与 `window_state` 最大化自适应、`Esc` / `F5` 快捷键 | `ui` | [verify_ui_layout.py](../scripts/verify_ui_layout.py) + `--smoke-test` |
