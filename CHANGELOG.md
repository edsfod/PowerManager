# 变更日志（Changelog）

本文件按时间倒序记录项目的架构演进、规则变更与历史迁移。

---

## v2.2.1 — 2026-11-25（发布脚本不再残留后台编译进程）
- **发布脚本**：`scripts/publish.ps1` 调用 `dotnet publish` 时加上 `/nodeReuse:false` 与 `/p:UseSharedCompilation=false`，发布结束后不再留下空闲十几分钟的 MSBuild 节点与 `VBCSCompiler` 进程；只作用于本次发布，不影响同一用户其他正在进行的编译。

## v2.2.0 — 2026-11-25（策略数字单一来源、文档与代码对齐、红线并入 ARCHITECTURE）
- **界面文案与通知的策略数字统一取自 `PowerPolicy`**：`get_state` 新增 `policy` 字段（`PolicySnapshot`），前端滑条范围、规则说明与提示文案中的数字都由它填入，不再手写；保持运行到期通知和主动睡眠备注改用 `PolicyText` 拼写；删除 `PowerPolicy` 中没有被使用的 4 组界面快捷档位常量（档位由前端 `app.js` 定义，超出范围自动隐藏）；新增 `UiPolicyConsistencyTests` 扫描前端手写的“数字 + 单位”，并校验 `mock.js` 的 `policy` 副本。
- **文档与代码对齐**：修正 [docs/design.md](docs/design.md) 中 `BatteryCharge` 的依赖描述、`pmctl` 权限描述与“界面/pmctl 不修改系统设置”的例外；明确 `restore-original` 只让常驻退出、不禁用计划任务；追踪矩阵改为同步 Named Pipe 描述并以 `pmctl status` 取代 v1 的 `--status`；[ADR-0003](docs/adr/0003-rejection-of-adaptive-charge.md) 补齐“未达标拔电满 10 分钟”回落条件。
- **补齐契约**：design.md 新增 JSON-RPC 方法表、Named Pipe 请求/响应字段、`requests/*.json` 字段、各状态文件字段与硬件任务失败语义；追踪矩阵的“验证方式”改为具体测试类与方法名前缀。
- **需求去实现化**：`NFR-03`、`NFR-04` 去掉“同一管道”“无宿主离线预览”等实现与开发细节。
- **README 重排**：按“快速开始 → 用法 → 升级/暂停/卸载 → 常见问题”组织，补充 `pmctl paths`。
- **发布脚本**：`scripts/publish.ps1` 在 `dist/` 中有 PowerManager 进程运行时提前报错并给出停止命令，不再删到一半失败。
- **删除 `docs/conventions.md`**：其中多数条目与需求、ADR、参考文档重复或属于通用常识。仍有效的部分并入 [ARCHITECTURE.md](ARCHITECTURE.md)：§1 改为带编号的红线 `R1`–`R6`（EC 只写 `03 10`、写后读回校验、不唤醒也不阻止睡眠、提权任务白名单校验、先存原始设置快照、不改关机语义），原有 4 条“不变量”删除；§5 新增工程与文档约定（实机验证、UTC、防文档漂移、隐私）。回滚的几条例外移到 [docs/design.md](docs/design.md) §五。
- **隐私**：`docs/investigations/` 改为仅本地保存（`.gitignore`），不再入库。

## v2.1.2 — 2026-11-25（边界缺陷修复、UI 交互与指标分组优化、UI 文档合并）
- **核心与交互缺陷修复**：
  - 修复前端进入保持运行草稿时 `0`（直到手动改回）被 `||` 误判回退为 `12` 小时的问题，以及 `set_mode` 失败时仍清空草稿、选中“直到手动改回”后直接点滑条原值不触发切换的边界问题。
  - 修复 CLI 切定时保持运行而 UI 偏好为 `0` 时滑条误显“直到手动改回”的问题（[UiStateFormatter.cs](src/PowerManager.UI/UiStateFormatter.cs) 优先由 `RunUntilUtc - ChangedAtUtc` 推导当前时长）。
  - 修复 [WakeSummaryCoordinator.cs](src/PowerManager.Agent/WakeSummaryCoordinator.cs) 中锁屏标志与待补发 Toast 之间的微小锁竞态。
  - 补齐 `pmctl mode` 用法提示中的 `0` 选项及 `WebRpcBridge` / `ARCHITECTURE.md` 中的 10 项 RPC 计数。
- **UI 体验与代码对齐**：
  - 宽屏（`>= 880px`）与最大化态隐藏规则折叠按钮且不再强行退出最大化；移除最大化态 `vh` 压缩/拉伸字号，全程保持固定基准字号跟随 Windows DPI。
  - 将 6 项电池指标按语义分为 **EC 硬件遥测（4 项：`2×2`/`4×1`）** 与 **Windows 电源模式（2 项：`2×1`）**；模式切换卡片改为语义 `<button role="radio">` 并补齐键盘焦点轮廓；清理 `styles.css` 未使用 Token 并让组件统一消费语义 Token；对齐 `mock.js` 文案与真实后端 `UiStateFormatter`。
- **UI 文档合并**：把 v2.1.1 在 `src/PowerManager.UI/docs/` 新增的三份独立文档合并为一份 [docs/ui-design.md](docs/ui-design.md)；在 [docs/requirements.md](docs/requirements.md) 新增 `NFR-03`、`NFR-04` 并纳入 [docs/design.md](docs/design.md) 与 `verify_doc_traceability.py`（21/21 双向追踪通过）。

## v2.1.1 — 2026-11-25（UI 独立文档、窗口贴合内容高度与对称布局、保持运行滑条与“直到手动改回”）
- **UI 独立文档（`src/PowerManager.UI/docs/`，已于 v2.1.2 合并进 `docs/ui-design.md`）**：新增与顶层 `docs/` 结构对称的 `requirements.md`（`UI-01`–`UI-08`）、`conventions.md`（谨慎硬编码、单向尺寸所有权、视觉焦点唯一、安全放弃、空间对称与双门禁验收）与 `design.md`（布局架构、草稿状态机与需求追踪矩阵），同步更新顶层文档导航与契约链接。
- **内容驱动窗口与最大化识别**：移除自由窗口的硬编码业务高度与 `vh` 字号循环，通过 `ResizeObserver` → `set_content_height` RPC 与垂直中心锚点实现对称贴合及切换还原原位，并以 `scrollbar-gutter: stable` 消除点击左右抖动；新增 `window_state` 事件识别最大化态，按视口比例缩放并垂直居中。
- **对称指标网格与布局重构**：将 6 项电池指标改为平级对称卡片（紧凑态 `2 × 3`，宽屏 `>= 880px` 自动重组为 `3 × 2`）；模式卡片 `#runbar` 独立位于底部通栏以消除展开态右侧留白，宽屏下所有滑条受最大宽度约束并与快捷档位同行排布。
- **保持运行草稿确认与“直到手动改回”**：全链路放开保持运行时长至 `1–24` 小时连续整数及 `0`（直到手动改回，更新 `SLP-02` 与 93 项单元测试）；前端采用与临时冲高一致的“滑条 + 快捷档位 + 确认”草稿态，单焦点虚线边框不全局变色，未确认放弃零副作用。
- **自动化布局验收**：新增 `scripts/verify_ui_layout.py`（基于系统 Edge 无头 Blink 引擎），校验 8 组自由/最大化视口的固有高度与零垂直溢出。

## v2.1.0 — 2026-11-25（文档代码地图、核心模块拆分与集成测试加固）
- **顶层架构地图（[ARCHITECTURE.md](ARCHITECTURE.md)）**：新增顶层代码地图、编译期依赖不变量、三条跨进程主链路及按修改场景的最小阅读清单，并在 [README.md](README.md) 与 `docs/conventions.md`（已删除）登记职责边界。
- **核心编排与 UI 模块拆分**：
  - `PowerManager.Agent` 从 `AgentController` 抽离出 `AgentSystemActions`（系统副作用与 dry-run）、`AgentAlarmScheduler`（闹钟汇总与冲高回落到期判断）与 `WakeSummaryCoordinator`（离开记录与锁屏暂存补发 Toast）。
  - `PowerManager.UI` 拆出 `UiModels`、`UiStateFormatter`（纯函数视图格式化）、`WebRpcBridge`（JSON-RPC 9 项桥接路由）、`WindowWidthAnimator`（DPI 尺寸与展开动画）及 `EmbeddedWebAssets`（内嵌静态资源拼装与 DWM 深浅色标题栏）；前端 `web/` 拆分为 `index.html`、`styles.css`、`mock.js`、`app.js`，零 Node 构建链，同时保留浏览器直接离线预览与单文件内嵌发布。
- **UI 契约与 IPC 测试补齐**：规范前端 Fluent Design Tokens 与独立离线 Mock 预览；新增 Named Pipe 端到端回环与多客户端并发集成测试（单元测试总数增至 92 项）；补充 `.gitattributes` 统一行尾。

## v2.0.0 — 2026-11-24（C# .NET 10 重写完成并切换至生产）
- **架构与 IPC 落地（[ADR-0006](docs/adr/0006-csharp-architecture-ipc-and-data-dir.md)）**：实现 `PowerManager.Core`、`PowerManager.Platform`、`PowerManager.Hardware.MateBook` 三层共享类库与 `PowerManager.Agent.exe`、`PowerManager.BatteryCharge.exe`、`PowerManager.UI.exe`、`pmctl.exe` 四个独立可执行文件；采用单用户 SID ACL Named Pipe（`UI / pmctl ↔ Agent`）与 `<DataDir>/requests/*.json` 原子白名单请求队列（`→ Battery-ChargeLimit` S4U 计划任务），运行时数据统一迁至 `%LOCALAPPDATA%\PowerManager`。
- **交互与通知完善**：`PowerManager.UI` 基于 WinForms + WebView2 实现单实例防多开、按需刷新 EC 读数、主动睡眠状态提示、系统深浅色标题栏热切换、纯前端本地倒计时与数据目录快捷入口；WinRT Toast 补齐静音（`<audio silent="true"/>`）、`Tag`/`Group` 去重覆盖与圆形图标。
- **发布、验证与切换**：新增统一 Framework-Dependent 发布脚本（`scripts/publish.ps1`）与零 Python 安装脚本（`install.ps1`，支持 `-ImportOriginalFrom` 只读导入 v1 `original.json`）；86 项单元测试与 19/19 需求双向追踪全部通过，实机完成生产切换与模式切换、EC 刷新、合盖诱导睡眠与醒来 Toast 验收。
- **远期规划**：新增 [docs/roadmap.md](docs/roadmap.md) 沉淀高频遥测、自适应调度、电池健康统计、新机型适配与交互式双向 Toast 五项远期演进方向。

## 2026-11-24（文档体系与验证基线重构）
- **独立重构目录**：在新开发目录 `PowerManager-v2`（即现在的本仓库）中重建清晰的正交文档体系，彻底剥离需求、设计、决策、参考事实与排查历史。
- **区分无时间性与有时间性资料**：将长期有效的硬件/平台事实收拢至 `docs/reference/`（[wmi-ec-interface.md](docs/reference/wmi-ec-interface.md)、[windows-standby-and-toast.md](docs/reference/windows-standby-and-toast.md)）；将带日期的故障案卷归入 `docs/investigations/`（后改为仅本地保存、不入库）。
- **建立决策与规范体系**：沉淀 5 份架构决策记录（`docs/adr/0001`–`0005`，含 C# .NET 10 技术栈选型）、工程与文档边界规范（`docs/conventions.md`，后已删除并入 `ARCHITECTURE.md`）及新机型移植指南（[docs/porting-guide.md](docs/porting-guide.md)）。
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
