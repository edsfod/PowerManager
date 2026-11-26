<!--
类别：实现状态。装：机制（调度、睡眠兜底、充电决策、通知、失败与回滚）与接口契约（JSON-RPC、Named Pipe、请求文件、状态文件）。
不装：模块划分与不变量（→ ARCHITECTURE.md）、需求数值（引用需求编号）、决策论证（→ adr/）。
具体数值见需求规格与 src/PowerManager.Core/Policy/PowerPolicy.cs；本文出现的实现参数与它的一致性由 DocPolicyConsistencyTests 校验。
-->

# 设计

界面相关的设计（布局、窗口行为、草稿确认状态机）见 [ui-design.md](ui-design.md)。

## 调度机制

### 事件来源

`Agent` 阻塞在 Win32 消息循环上，只响应以下来源（不变量 `A4`，[ADR-0004](adr/0004-event-driven-scheduling.md)）：

| 来源 | 用途 |
|---|---|
| 合盖 / 开盖、显示器开 / 关 | 判断离开与回来；合盖时用 `QueryDisplayConfig` 判断是否有外接活动显示器（合盖动作设为"不采取任何操作"后，系统不再发送显示器关闭通知） |
| 插电 / 拔电、电量每变 1% | 切换电源模式（PWR-01、PWR-02）；电量到 5% 的倍数时触发 `BatteryCharge` 记录历史（CHG-03）；驱动冲高计时与通勤温度检查的启停 |
| 会话锁屏 / 解锁（`WTSRegisterSessionNotification`） | 解锁后再发醒来通知（NTF-01） |
| Named Pipe 请求 | 模式切换（SLP-01、SLP-02） |
| 定时器到期 | 见下 |

不监听 `PBT_APMSUSPEND` / `PBT_APMRESUMEAUTOMATIC`：现代待机下桌面进程收不到可靠的挂起 / 恢复广播（[windows-standby-and-toast.md](reference/windows-standby-and-toast.md)）。

### 单一定时器

所有未来时间点由 `AgentAlarmScheduler` 合并到一个 Waitable Timer，设为最早到期时刻（`fResume: false`，不唤醒电脑，安全约束 `C3`）：

- 普通模式合盖后的睡眠延迟（SLP-06）；
- 保持运行到期（SLP-02）；
- 冲高保持满、冲高最长兜底、拔电回落（CHG-02）；
- 通勤温度检查，每 3 分钟一次（SLP-05）；
- 主动睡眠检查，每 1 分钟一次（SLP-10、SLP-11）。

**冻结检测**：定时器实际触发时刻晚于目标墙上时间 30 秒以上，判定进程刚经历冻结（睡眠或休眠）。醒来后按墙上时间重算剩余闹钟，并检查是否属于合盖状态下的意外唤醒（SLP-11）。

## 电源设置

- 通过 `powrprof.dll` 写关屏、睡眠超时与合盖、电源键动作：`PowerWriteACValueIndex` / `PowerWriteDCValueIndex` 写入后，调用 `PowerSetActiveScheme` 才生效。
- 通过 `PowerSetUserConfiguredACPowerMode` / `PowerSetUserConfiguredDCPowerMode` 切换电源模式。
- 以上均不需要管理员权限，也不启动外部进程。
- 合盖与电源键动作在两种模式下都设为"不采取任何操作"（SLP-05、SLP-06、SLP-08；[ADR-0002](adr/0002-modern-standby-fallback.md)）。合盖后的行为全部由 `Agent` 决定。
- Windows 自带的低电量自动休眠设置保持不变。

## 主动睡眠与休眠兜底

本机没有 S3 睡眠，程序无法直接请求进入现代待机（[ADR-0002](adr/0002-modern-standby-fallback.md)）。"主动睡眠"的做法是：临时把关屏与睡眠超时缩短到 10 秒 / 15 秒，等系统因闲置进入现代待机。

**进入主动睡眠**的情形：

| 情形 | 条件 | 需求 |
|---|---|---|
| 普通模式合盖 | 合盖且无外接活动显示器，持续满延迟时长；或保持运行到期时正处于合盖 | SLP-06 |
| 通勤闲置 | 保持运行、用电池，系统闲置超过睡眠超时再加 2 分钟余量仍未睡下 | SLP-05 |
| 通勤过热 | 每 3 分钟触发 `BatteryCharge` 读温度，10 秒后读 `state.json`；温度达到阈值、读数未过期（`updated_at_utc` 距今不超过 120 秒），且不处于"开盖且近期有键鼠操作" | SLP-05 |
| 合盖意外唤醒 | 冻结检测发现合盖状态下醒来，且不是"保持运行 + 插电"；发现即进入 | SLP-11 |

**主动睡眠期间**每 1 分钟检查一次（只在系统醒着时运行）：

1. 开盖且 60 秒内有键鼠操作：中止，恢复当前模式的超时设置。
2. 持续 2 分钟仍未入睡（SLP-10、SLP-11）：在当前进程令牌启用 `SeShutdownPrivilege`，调用 `SetSuspendState(Hibernate)`。
3. 休眠请求被系统拒绝：重置计时，下一轮再试。

开盖或醒来后恢复当前模式的超时设置。每次主动动作都写一条备注，供醒来通知使用。

## 充电控制

`BatteryCharge` 每次运行：

1. 按文件名顺序消费 `requests/` 中的请求（契约见下文，校验见安全约束 `C4`）。同一批有多条冲高请求时，以最后一条为准。
2. 读当前阈值（`03 11`）、电池状态与温度传感器。
3. 由 `ChargeDecisionEngine` 决定目标阈值：
   - 未冲高：CHG-01 的日常阈值（[ADR-0003](adr/0003-rejection-of-adaptive-charge.md)）；
   - 冲高中：`(目标 - 5)% – 目标%`。
4. 读数与目标不一致或读数异常时，写 `03 10`，再读 `03 11` 校验（安全约束 `C2`）。
5. 写 `state.json`、`history.log`、`ec.log`。

**冲高状态**（字段见 `state.json` 的 `boost`）：

- `reached_target`：首次达到目标后置为 `true`，之后不再变回。
- `full_since_utc`：达标且插电的**连续**计时起点；插电期间电量跌破目标或拔电即清空，重新计时。
- `unplugged_since_utc`：未达标时拔电的计时起点。
- 回落条件见 CHG-02；回落时写入 `reverted_at_utc` 与 `last_reason`。

**重启复位**：EC 阈值在重启后复位为 `95–100`。`Battery-ChargeLimit` 用 S4U 方式运行（不需要用户登录），并绑定开机与唤醒触发器，在登录前就写回日常阈值（CHG-01）。

硬件指令的字节契约见 [wmi-ec-interface.md](reference/wmi-ec-interface.md)。

## 醒来通知

离开超过 1 分钟后开盖亮屏时（NTF-01），后台线程延迟 15 秒查询事件日志，时间窗口为 `[离开时刻 - 5 秒, 回来时刻 + 15 秒]`：

| 事件 | 用途 |
|---|---|
| `Power-Troubleshooter` 1，`TargetState=5` | 有则判定为"休眠" |
| `Kernel-Power` 507，`SleepEntered=true` | 累计真实睡眠时长 |
| `Kernel-Power` 506 / 507 | 入睡与唤醒原因；506 排除 `Reason=20/25`、507 排除 `Reason=20`，不计为短暂唤醒 |
| `Kernel-Power` 42 | 随查询一并读取，摘要逻辑不使用 |

- 离开期间有主动动作备注时，首条备注作为入睡原因，其余备注附在第二行。
- 状态为睡眠或休眠、且期间累计醒着 ≥ 120 秒时，附加"醒着共 X 分钟"。
- 锁屏中则暂存，解锁后发送。

**Toast 规格**：WinRT Toast API；`AppUserModelID = "PowerManager"`、`Group = "PowerManager"`；`Tag` 按类别区分（醒来汇总 `wake`、保持运行到期 `mode`），新通知覆盖同类旧通知；`<audio silent="true"/>`；标题 `hint-maxLines="1"`；程序目录有 `icon.png` 时作为圆形图标（`appLogoOverride`）。横幅显示依赖开始菜单快捷方式携带的 AppUserModelID（[windows-standby-and-toast.md](reference/windows-standby-and-toast.md)）。

## 接口契约

### 界面宿主与前端（JSON-RPC）

- `PowerManager.UI.exe` 用 WebView2 加载内嵌前端，用户数据目录为 `<DataDir>/ui/webview2/`。
- 单实例：`Mutex` 名为 `Local\PowerManager.UI.<pipeName>`，重复启动时激活已有窗口。
- 命令行参数：`--debug`、`--data-dir`、`--pipe-name`、`--dry-run`、`--smoke-test`。
- 消息格式：前端 `{ id, method, params }`，宿主 `{ id, result, error }`。宿主主动推送 `{ event: "focus" }`（窗口激活）与 `{ event: "window_state", maximized }`。
- 除 `get_state` 外，方法都返回 `UiActionResult { ok, msg? }`。路由见 `WebRpcBridge`。

| 方法 | 参数 | 说明 |
|---|---|---|
| `get_state` | — | 返回 `UiStateView`，顶层字段：`agent`、`dry_run`、`forced_sleep`、`rules_expanded`、`data_dir`、`mode`、`power_mode`、`battery`、`boost`、`boost_prefs`、`policy`。子结构见 `UiModels`；`policy` 见 `PolicySnapshot` |
| `set_mode` | `mode`（`"normal"` / `"run"`）、`hours`（`0` 表示直到手动改回） | 转发为 Named Pipe `SetMode` |
| `boost` | `target`、`hold`（分钟）、`maxh`（小时） | 写冲高请求文件并触发 `Battery-ChargeLimit` |
| `refresh_ec` | — | 写 `reason = "ui-refresh"` 的触发请求并触发 `Battery-ChargeLimit` |
| `open_data_dir` | — | 打开 `<DataDir>` |
| `start_agent` | — | 启动计划任务 `PowerManager-Agent` |
| `set_expanded` | `expanded` | 保存规则面板展开状态 |
| `set_content_height` | `height` | 前端上报内容高度，宿主据此贴合窗口 |
| `close_window` | — | 关闭窗口 |
| `smoke_ready` | — | `--smoke-test` 下的前端就绪信号 |

界面与 `pmctl` 只读取 `mode.json`、`state.json`、当前电源模式与 `BatteryStatus`，不直接修改电源设置（不变量 `A5`）。

### Named Pipe：`UI` / `pmctl` ↔ `Agent`

- 管道名 `\\.\pipe\PowerManager.Agent`（可用 `--pipe-name` 覆盖），只允许当前用户 SID 访问。
- 每次连接交换一轮：客户端写一行 UTF-8 JSON 请求；`Agent` 投递到主消息循环执行，写回一行 JSON 响应。
- 类型定义见 `AgentIpcRequest` / `AgentIpcResponse`（`Core/Contracts/IpcContracts.cs`）。

| 方向 | 字段 | 取值 |
|---|---|---|
| 请求 | `command` | `"Ping"` / `"GetStatus"` / `"SetMode"` / `"RestoreOriginal"` |
| 请求 | `mode` | 仅 `SetMode`：`"Normal"` / `"KeepRunning"`（与 `mode.json` 的 `normal` / `keep_running` 写法不同） |
| 请求 | `duration_hours` | 仅 `SetMode` + `KeepRunning`：`0` 或 SLP-02 的时长范围；缺省取默认时长 |
| 响应 | `ok`、`message` | 是否成功；可读的结果或失败原因 |
| 响应 | `mode_state` | 当前 `mode.json` 内容 |
| 响应 | `forced_sleep_active`、`data_dir` | 是否处于主动睡眠；`Agent` 实际使用的数据目录 |

`RestoreOriginal`：按 `original.json` 写回电源设置并让 `Agent` 退出，但不禁用计划任务 `PowerManager-Agent`，下次登录时 `Agent` 重新启动并应用当前模式。`Agent` 未运行时，`pmctl restore-original` 直接按 `original.json` 写回。

### 请求文件：普通权限进程 → `Battery-ChargeLimit`

调用方在 `<DataDir>/requests/` 先写临时文件，再原子重命名为 `<utc-ticks:D20>-<guid>.json`，然后触发计划任务 `Battery-ChargeLimit`（`MultipleInstances=Queue` 保证串行）。

| 字段 | 取值 |
|---|---|
| `kind` | `"Trigger"`（只刷新与记录）或 `"Boost"`（发起冲高） |
| `reason` | 非空，≤ 120 字符（如 `ui-refresh`、`电量 75%`、`通勤查温`、`pmctl boost 95%`） |
| `boost` | `kind = "Boost"` 时必填，`"Trigger"` 时必须为空：`{ target_stop, hold_minutes, max_hours }`，边界为 CHG-02 的规格 |
| `created_at_utc` | 请求创建时间 |

校验失败的请求记入 `ec.log` 后丢弃；无论成败，消费后都删除请求文件。

## 持久化

`<DataDir>` 生产环境为 `%LOCALAPPDATA%\PowerManager`。`install.ps1` 把展开后的绝对路径通过 `--data-dir` 写进计划任务参数，因为开机未登录时 S4U 任务无法解析用户环境变量。

| 文件（相对 `<DataDir>`） | 类型 | 用途 |
|---|---|---|
| `agent/original.json` | 持久 | 首次修改电源设置前保存的 Windows 原始设置（安全约束 `C5`），供恢复使用；格式与 v1 兼容，可用 `install.ps1 -ImportOriginalFrom` 导入 |
| `agent/mode.json` | 持久 | 当前模式与保持运行到期时刻 |
| `agent/agent.log`、`error.log` | 日志 | 常驻事件流水（含醒来通知内容）、异常堆栈 |
| `battery-charge/state.json` | 缓存 | 最新 EC 读数、冲高状态 |
| `battery-charge/ec.log`、`history.log`、`error.log` | 日志 | 阈值改写记录、每次运行的电量 / 功率 / 温度快照（CHG-03）、异常堆栈 |
| `requests/*.json` | 瞬态 | 待 `Battery-ChargeLimit` 消费的请求 |
| `ui/prefs.json` | 持久 | 界面偏好 |
| `ui/webview2/` | 缓存 | WebView2 用户数据 |

日志超过 1 MB 轮换为 `.1`。

**字段**（JSON；时间戳为 UTC ISO 8601；读取时字段名不区分大小写；类型见 `Core/Models/` 与 `UiModels`；向后兼容由 `SchemaCompatibilityTests` 守护）：

| 文件 | 字段 |
|---|---|
| `agent/original.json` | `ac` / `dc`：各含 `display`、`sleep`（秒，`0` 为从不）、`lid`、`button`（Windows 电源动作枚举值）；`saved_at_utc` |
| `agent/mode.json` | `mode`（`"normal"` / `"keep_running"`）；`run_until_utc`（`null` 表示直到手动改回或普通模式）；`changed_at_utc` |
| `battery-charge/state.json` | `updated_at_utc`；`ec`（`hw_ok`、`hw_start`、`hw_stop`、`battery_c`、`system_w`、`charge_ma`、`sensors_c`、`hottest_c`，原始字段 `charge_mode_raw`、`resume_flag_raw`；读取失败的项为 `null`）；`os_power`（`ac_online`、`percent`）；`boost`（`active`、`target_start`、`target_stop`、`full_hold_minutes`、`max_total_hours`、`started_at_utc`、`deadline_utc`、`full_since_utc`、`reached_target`、`unplugged_since_utc`、`reverted_at_utc`、`last_reason`）；`target`（`[启充, 停充]`）；`win`（`BatteryStatus` 快照，可为 `null`）；`run_reason` |
| `ui/prefs.json` | `hours`、`boost_target`、`boost_hold`、`boost_maxh`、`rules_expanded` |

## 失败与回滚

**硬件任务失败**：写阈值后读回不一致时，`ec.log` 记 `MISMATCH`，堆栈写入 `error.log`，以退出码 `2` 结束，不更新 `state.json`；其他异常以退出码 `1` 结束。界面上"EC 上次更新时间"停止前进，是硬件任务失败的主要外部信号。

**回滚边界**：Windows 电源设置只在用户主动停用（`pmctl restore-original` 或卸载）时按 `original.json` 恢复。以下情况刻意不回滚：

- **`Agent` 异常退出**：已写入的设置保持不变，等计划任务重启 `Agent`（失败后每 1 分钟重试，最多 5 次）。`Agent` 启动时按当前模式重写一次设置；若崩溃发生在主动睡眠期间，10 秒 / 15 秒的临时超时会保留到 `Agent` 重新启动。
- **EC 阈值**：重启后 EC 复位为 `95–100`，目标始终是日常阈值，所以不保存也不恢复"原始阈值"，由开机、唤醒等触发器重新写入。

## 计划任务

| 任务 | 运行身份 | 触发 | 设置 |
|---|---|---|---|
| `PowerManager-Agent` | 当前用户 | 登录 | 无执行时限（`PT0S`）；多实例 `IgnoreNew`；允许电池运行；失败每 1 分钟重启，最多 5 次 |
| `Battery-ChargeLimit` | 管理员（S4U，最高权限） | 开机、登录、唤醒（`Power-Troubleshooter` 1）、插拔电（`Kernel-Power` 105）、显式触发 | 执行时限 `PT2M`；多实例 `Queue`；允许电池运行 |
