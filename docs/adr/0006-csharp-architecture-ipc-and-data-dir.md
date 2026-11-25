# ADR-0006: C# 多项目多可执行文件边界、IPC 契约与运行时数据目录选型

- **状态**：Accepted
- **日期**：2026-11-24
- **关联决策**：[ADR-0001](0001-scope-and-single-model.md)、[ADR-0004](0004-event-driven-migration.md)、[ADR-0005](0005-choose-csharp-framework-dependent.md)

## 背景
按 [ADR-0005](0005-choose-csharp-framework-dependent.md) 确定使用 C#（`.NET 10 SDK` + Framework-Dependent）重写主程序后，需要在编码前确定四项工程结构与跨进程交互决策：
1. 产物形态：单一 exe 子命令分发，还是按职责拆分多项目与多个独立 exe；
2. EC 提权承载形态：维持按需 S4U 计划任务，还是改为常驻 Windows Service；
3. 跨进程通信（IPC）与命令行交互载体：`UI / CLI ↔ Agent` 以及 `Agent / UI / CLI → Battery-ChargeLimit` 的通信方式，以及 `WinExe` 后台进程在终端查看状态的兼容问题；
4. 运行时状态与日志目录位置：沿用项目内 `var/`，还是独立存放在 `%LOCALAPPDATA%\PowerManager`。

## 决策
1. **单一解决方案 + 多项目分层 + 4 个独立可执行文件**（发布至统一目录共享底层 DLL）：
   - 类库：`PowerManager.Core`（纯函数核心、只读策略表、Schema、硬件抽象接口）、`PowerManager.Platform`（通用 Windows 11 Win32/WinRT/IPC 适配）、`PowerManager.Hardware.MateBook`（MateBook 14 2024 WMI/EC 适配）；
   - 可执行文件：`PowerManager.Agent.exe`（`WinExe`，后台常驻）、`PowerManager.BatteryCharge.exe`（`Exe`，S4U 提权硬件任务与直接 CLI）、`PowerManager.UI.exe`（`WinExe`，WebView2 界面）、`pmctl.exe`（`Exe`，薄控制台统一命令行入口）；
   - 测试：`PowerManager.Tests`（xUnit，覆盖 `Core` 纯函数与 Schema 校验）。
2. **EC 控制维持 S4U 计划任务（`Battery-ChargeLimit`，`MultipleInstances=Queue`）**，不改为常驻 Windows Service。
3. **显式结构化 IPC 契约**：
   - `UI / pmctl ↔ Agent`（同用户普通权限）：采用仅限当前用户 SID 访问的 Named Pipe（`\\.\pipe\PowerManager.Agent`）+ JSON 请求/响应；
   - `Agent / UI / pmctl → Battery-ChargeLimit`（普通权限 → 提权 S4U 任务）：在 `<DataDir>/requests/` 下原子写入结构化 JSON 请求文件（传递触发原因或冲高参数）并触发 `Battery-ChargeLimit`；提权任务消费请求时强制执行数值白名单校验并拒绝符号链接/重解析点。
4. **运行时数据目录采用 `%LOCALAPPDATA%\PowerManager`（配合 `--data-dir` 显式覆盖）**：
   - 安装脚本 `install.ps1` 将展开后的 `%LOCALAPPDATA%\PowerManager` 通过 `--data-dir` 显式写入计划任务参数，避免开机未登录阶段 S4U 任务解析用户环境变量失败；
   - 开发与切换前验证阶段通过 `--data-dir ./var` 隔离，与生产数据互不干扰；
   - `original.json` 保持与 v1 完全兼容的 JSON Schema，生产切换时只读导入原快照，避免覆盖真实 Windows 原始电源设置。

## 依据
1. **编译期强制依赖隔离与 Windows 子系统匹配**：拆分为独立 exe 后，`Agent`、`UI`、`pmctl` 在编译期即无法引用 `Hardware.MateBook`，`BatteryCharge` 不携带 WebView2 依赖；同时 `Agent`/`UI` 使用 `WinExe` 消除登录与启动黑框，新增薄控制台 `pmctl.exe`（只做参数解析与 IPC 转发，体积小、无常驻进程）彻底规避 `WinExe + AttachConsole` 在 PowerShell / Git Bash 中不等待退出、提示符错位与重定向失效的缺陷。
2. **S4U 计划任务的工程风险与收益比更优**：本项目日均 EC 触发仅约 30 次，1–2 秒冲高延迟与通勤 10 秒查温等待对体验和热保护均无实质影响；维持 S4U 计划任务可直接复用本机已验证的开机/登录/唤醒/插拔电触发器，避免引入 `LocalSystem` 常驻权限面、跨账户 Pipe ACL、文件所有权与升级停服摩擦。
3. **数据与代码物理分离**：`original.json` 与 `history.log`/`ec.log` 属于不可再生资产，放入 `%LOCALAPPDATA%\PowerManager` 可避免仓库清理、重编译或迁往 `Program Files` 只读目录时误删或权限报错；而 `C:\ProgramData` 默认对普通用户只读，不适合作为普通权限 `Agent`/`UI` 的写入目录。

## 影响
- [design.md](../design.md) 与 [README.md](../../README.md) 同步补全多项目结构、`pmctl.exe` 用法、IPC 契约与 `%LOCALAPPDATA%\PowerManager` 路径约定。
- **未来演进边界**：若未来新增高频实时遥测（如秒级功耗/多点温度曲线或自适应调度采样），只需将最外层的 `PowerManager.BatteryCharge` 计划任务外壳替换为常驻 Windows Service 与 Named Pipe 服务端，`PowerManager.Core` 与 `PowerManager.Hardware.MateBook` 无需重写。
