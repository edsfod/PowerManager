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
| **命令行工具（`pmctl`）** | `pmctl.exe`（`Exe`） | 普通用户（直写 EC 时可提权） | 终端按需手动执行 | 薄控制台入口，统一承接 `status`、`restore-original`、`boost` 等终端查看与控制命令，避免 `WinExe` 后台进程在终端输出错位 |

**依赖方向约束**：编译期强制隔离——`PowerManager.Agent`、`PowerManager.UI`、`pmctl` 仅引用 `Core` 与 `Platform`，严禁引用 `PowerManager.Hardware.MateBook` 或包含厂商专有 WMI 字节/EC 寄存器逻辑；`PowerManager.BatteryCharge` 仅引用 `Core` 与 `Hardware.MateBook`，不携带 WebView2 依赖。普通权限进程仅通过 `<DataDir>/requests/` 原子 JSON 请求文件 + 触发 `Battery-ChargeLimit` 与状态文件 `state.json` 和硬件任务交互。

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

| 配置项 | 普通模式 | 保持运行模式（2/4/8/12/24 小时，默认 12 小时） |
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

目前 `ui` 与 `pmctl` 的设计集中归口在本节，不单独拆分子目录文档：

- **界面宿主与标识**：`PowerManager.UI.exe` 由 `Microsoft.Web.WebView2` 挂载系统自带 WebView2 渲染单页前端（`web/index.html` 作为 `EmbeddedResource` 内嵌，用户数据目录隔离在 `<DataDir>/ui/webview2/`，不污染程序目录）；进程启动时主动调用 `SetCurrentProcessExplicitAppUserModelID('PowerManager')`，使任务栏图标与通知发送者标识一致；通过按管道名隔离的当前用户单实例 `Mutex`（`Local\PowerManager.UI.<pipeName>`）防止重复多开，再次启动时激活已有窗口；支持 `--debug`（开启开发者工具）、`--data-dir`、`--pipe-name`、`--dry-run` 与 `--smoke-test` 参数。
- **前端桥接契约与独立预览**：宿主与前端之间通过 WebView2 JSON-RPC 通信——前端发送 `{ id, method, params }`，宿主回传 `{ id, result, error }`；窗口重新激活时宿主主动推送 `{ event: "focus" }` 触发刷新。桥接方法共 9 项：`get_state`（返回 `UiStateView` 状态快照）、`set_mode`（`mode`, `hours`）、`boost`（`target`, `hold`, `maxh`）、`refresh_ec`、`open_data_dir`、`start_agent`、`set_expanded`（`expanded`）、`close_window`、`smoke_ready`（其余操作均返回 `UiActionResult { ok, msg? }`）。前端样式通过语义化 CSS Design Tokens（表面、文字、状态色、圆角、间距、动效）组织，且在无 `window.chrome.webview` 宿主环境时自动回落至内置 Mock 桥接，支持直接用浏览器打开 `web/index.html` 离线预览各运行态与主题切换。
- **状态读取**：界面与 `pmctl` 只读展示 `<DataDir>/agent/mode.json`（当前模式与到期时间）、`<DataDir>/battery-charge/state.json`（当前 EC 阈值、冲高倒计时与温度），以及直接读取非提权的当前电源模式（`powrprof`）与标准 `BatteryStatus`；界面用户偏好（保持运行时长、冲高参数、规则面板展开状态）保存于 `<DataDir>/ui/prefs.json`。
- **跨进程通信（IPC）契约（[ADR-0006](adr/0006-csharp-architecture-ipc-and-data-dir.md)）**：界面与 `pmctl` 以普通权限运行，不直接修改系统设置，也不弹出 UAC 确认框：
  - **`ui / pmctl ↔ agent`（同步 Named Pipe）**：`agent` 监听仅限当前用户 SID 访问的 Named Pipe（`\\.\pipe\PowerManager.Agent`），接收 JSON 命令（切换普通/保持运行模式、查询实时状态含主动睡眠状态、执行 `--restore-original` 停用恢复），投递至主消息循环执行后同步返回结构化 JSON 回执（成功/失败原因）。
  - **`agent / ui / pmctl → Battery-ChargeLimit`（原子请求文件 + 计划任务队列）**：调用方在 `<DataDir>/requests/` 目录先写临时文件再原子重命名为 `<utc-ticks>-<guid>.json`（携带按需刷新触发原因如 `ui-refresh`，或临时冲高参数：目标 `85%–100%`、保持 `15–180` 分钟、最长 `1–12` 小时；界面提供滑动条与快捷档位：目标 `85/90/95/100%`，保持 `30/60/120` 分钟默认 `60`，最长 `2/4/8` 小时默认 `4`，另提供按需刷新读数按钮，不进行后台轮询），随后触发计划任务 `Battery-ChargeLimit`（由 `MultipleInstances=Queue` 保证串行执行）。提权任务消费请求时强制校验数值白名单边界并拒绝符号链接/重解析点，消费完毕即删除请求文件。

---

## 五、 状态持久化与安全回滚

生产环境运行时状态、请求与日志统一存放在 **`%LOCALAPPDATA%\PowerManager`**（记作 `<DataDir>`，不进入 Git）；`install.ps1` 注册计划任务时将展开后的绝对路径通过 `--data-dir` 显式写入任务参数（避免开机未登录阶段 S4U 任务解析用户环境变量失败），开发与验证阶段通过 `--data-dir ./var` 隔离。

| 文件（相对于 `<DataDir>`） | 类型 | 用途与回滚机制 |
|---|---|---|
| `agent/original.json` | 持久 | 首次改动电源设置前保存的 Windows 原始超时与动作快照（Schema 与 v1 兼容，支持切换时只读导入），供 `pmctl restore-original` 一键恢复并停用 |
| `agent/mode.json` | 持久 | 当前模式（普通 / 保持运行）与保持运行到期时间戳 |
| `agent/agent.log` / `error.log` | 日志 | 常驻事件流水（含醒来通知内容，UTC，超 1 MB 轮换）与异常堆栈 |
| `battery-charge/state.json` | 缓存 | 最新 EC 读数、冲高目标与保持计时状态 |
| `battery-charge/ec.log` / `history.log` / `error.log` | 日志 | EC 阈值改写记录、每次运行的电量/功率/温度快照（UTC，`CHG-03`；`history.log` 超 1 MB 轮换 `.1`）与异常堆栈 |
| `requests/*.json` | 瞬态 | 普通权限进程投递给 `Battery-ChargeLimit` 的原子 JSON 触发/冲高请求文件，消费后删除 |
| `ui/prefs.json` | 持久 | 界面用户偏好（保持运行时长、冲高默认参数、规则面板展开状态） |
| `ui/webview2/` | 缓存 | WebView2 运行时用户数据目录（与可执行目录隔离） |

---

## 六、 可测试性与纯函数边界

所有不涉及 Win32/WMI 副作用的核心决策必须实现为纯函数并配独立单元测试：
- 模式到系统超时/电源模式参数的换算、闹钟到期时刻计算、醒来事件日志解析与摘要拼装；
- 冲高开始、达标保持计时与回落条件判定（`decide()`）。

---

## 七、 需求追踪矩阵（Traceability Matrix）

可通过 [verify_doc_traceability.py](../scripts/verify_doc_traceability.py) 自动校验本矩阵与 [requirements.md](requirements.md) 的编号集合完全一致：

| 需求编号 | 设计实现机制 | 归属模块 | 验证方式 |
|---|---|---|---|
| `SLP-01` | UI 向常驻发起异步模式切换请求，无自动实验检测 | `ui` / `agent` | 纯函数单测 + 手动切换核验 |
| `SLP-02` | `mode.json` 持久化到期时间 + Waitable Timer 到点回落普通模式 | `agent` | 纯函数单测 + 状态检查 |
| `SLP-03` | 普通模式通过 `powrprof.dll` 写入插电/电池关屏与睡眠超时 | `agent` | 纯函数单测 + `--status` 核验 |
| `SLP-04` | 保持运行插电设置 15 分钟关屏、睡眠设为从不 | `agent` | 纯函数单测 + `--status` 核验 |
| `SLP-05` | 保持运行电池合盖不采取操作、30 分钟睡眠、每 3 分钟读温度 ≥ 40 °C 触发睡眠 | `agent` / `battery-charge` | 纯函数单测 + 温度触发测试 |
| `SLP-06` | 合盖且 `QueryDisplayConfig` 无外接活动屏 1 分钟后临时缩短超时 | `agent` | 纯函数单测 + 实机合盖测试 |
| `SLP-07` | 闲置关屏超时独立于睡眠超时（现代待机仅熄屏不冻结后台） | `agent` | [verify_power_boundaries.py](../scripts/verify_power_boundaries.py) |
| `SLP-08` | 电源键系统动作设为“不采取任何操作” | `agent` | `--status` + [verify_power_boundaries.py](../scripts/verify_power_boundaries.py) |
| `SLP-09` | 不劫持系统关机流程，保留原生关机语义 | `agent` | 实机开始菜单关机核验 |
| `SLP-10` | 主动触发睡眠 2 分钟后仍唤醒则调用 `SetSuspendState(Hibernate)` | `agent` | 纯函数单测 + 高负载合盖实测 |
| `SLP-11` | Waitable Timer 迟到 > 30 秒判定冻结醒来，若合盖且非“保持运行+插电”，2 分钟后重睡或休眠 | `agent` | 纯函数单测 + 实机唤醒核验 |
| `PWR-01` | 交流电事件触发 `PowerSetUserConfiguredACPowerMode` 为 `Best Performance` | `agent` | `--status` + 拔插电切换核验 |
| `PWR-02` | 电池电量变化通知跨过 50% 时调用 `PowerSetUserConfiguredDCPowerMode` | `agent` | 纯函数单测 + 电量阈值核验 |
| `CHG-01` | `03 10` 写入 `75–80%`，S4U 计划任务于开机/登录/唤醒/插拔电时恢复 | `battery-charge` | 纯函数单测 + [verify_wmi_readonly.py](../scripts/verify_wmi_readonly.py) |
| `CHG-02` | 冲高写入 `(上限-5)%–上限%`，Waitable Timer 与插拔电触发 4 条件回落 | `battery-charge` / `agent` | 纯函数单测（`decide()`）+ `--status` |
| `CHG-03` | 电量每变 5%、插拔电、唤醒及写阈值时追加 `history.log` 与 `ec.log` | `battery-charge` / `agent` | 检查 `history.log` 与 `ec.log` |
| `NTF-01` | 开盖亮屏延后 15 秒查 `wevtapi`，解锁后通过 .NET 原生 WinRT Toast API 发通知 | `agent` | 合盖 >1 分钟开盖实测 + `agent.log` |
| `NFR-01` | 常驻阻塞等待 Win32 事件 + 单一 Waitable Timer；EC 任务去定时化 | `agent` / `battery-charge` | 计划任务触发器检查 |
| `NFR-02` | `ui` 整合模式切换与冲高控制，桌面与开始菜单统一 `PowerManager.lnk` | `ui` / `install.ps1` | 快捷方式与界面操作核验 |
