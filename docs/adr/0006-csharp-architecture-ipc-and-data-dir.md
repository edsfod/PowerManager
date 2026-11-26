<!--
类别：决策状态。决策被推翻时原地改写本文，被推翻的方案及原因写进"备选方案"。
-->

# ADR-0006: 多项目多可执行文件、S4U 硬件任务、两条 IPC 通道、数据目录放 `%LOCALAPPDATA%`

## 背景

在 C#（[ADR-0005](0005-choose-csharp-framework-dependent.md)）下需要确定四件事：产物形态；需要管理员权限的 EC 控制由什么承载；进程间怎么通信；运行时状态与日志放在哪。

驱动力：

- 写 EC 需要管理员权限，其余功能不需要；提权面越小越好（[ADR-0001](0001-scope-and-single-model.md) 的硬件隔离）。
- EC 触发频率低，日均约 30 次（[ADR-0004](0004-event-driven-scheduling.md)）；开机、登录、唤醒、插拔电触发器已在本机验证可用。
- 后台进程应为 `WinExe`（启动不闪黑框），但 `WinExe` 在终端里输出状态有缺陷：`WinExe + AttachConsole` 在 PowerShell / Git Bash 中不等待退出，提示符错位，重定向失效。
- `original.json` 与日志是不可再生的数据。

## 决策

1. **单一解决方案、多项目分层、4 个可执行文件**，发布到同一目录共享类库：
   - 类库：`PowerManager.Core`（纯函数、策略表、契约、硬件接口）、`PowerManager.Platform`（Windows 封装、IPC、存储）、`PowerManager.Hardware.MateBook`（本机型 WMI / EC）；
   - 可执行文件：`PowerManager.Agent.exe`（`WinExe`，常驻）、`PowerManager.BatteryCharge.exe`（`Exe`，S4U 硬件任务，也可在管理员终端直接运行）、`PowerManager.UI.exe`（`WinExe`，WebView2 界面）、`pmctl.exe`（`Exe`，命令行）；
   - 测试：`PowerManager.Tests`（xUnit）。
2. **EC 控制由 S4U 计划任务 `Battery-ChargeLimit` 承载**（`MultipleInstances=Queue`）。
3. **两条 IPC 通道**：
   - 同权限的 `UI` / `pmctl` ↔ `Agent`：只允许当前用户 SID 访问的 Named Pipe，JSON 请求 / 响应；
   - 普通权限 → 提权任务：在 `<DataDir>/requests/` 原子写入 JSON 请求文件并触发 `Battery-ChargeLimit`，提权任务做白名单校验并拒绝重解析点。
4. **数据目录为 `%LOCALAPPDATA%\PowerManager`**，可用 `--data-dir` 覆盖：`install.ps1` 把展开后的路径写进计划任务参数（开机未登录时 S4U 任务无法解析用户环境变量）；开发时用 `--data-dir ./var` 隔离；`original.json` 保持与 v1 兼容，可只读导入。

契约细节见 [design.md](../design.md)。

## 备选方案

1. **单个 exe 加子命令**：做不到编译期隔离（`Agent`、`UI` 无法被禁止引用硬件层，`BatteryCharge` 会带上 WebView2）；子系统只能二选一，`WinExe` 会出现上述终端缺陷，`Exe` 会在登录时闪黑框。
2. **常驻 Windows Service 承载 EC 控制**：引入 `LocalSystem` 常驻权限面、跨账户管道 ACL、文件所有权与升级时停服的问题；按日均约 30 次的频率，1–2 秒的冲高延迟与通勤查温的 10 秒等待都没有实际影响。
3. **数据放在仓库内 `var/`**：清理仓库、重新编译或迁往 `Program Files` 只读目录时可能误删或无权写入。
4. **数据放在 `C:\ProgramData`**：默认对普通用户只读，普通权限的 `Agent` / `UI` 无法写入。

## 影响

- 普通权限进程从编译期就不可能直写 EC（[ARCHITECTURE.md](../../ARCHITECTURE.md) 不变量 `A2`、`A3`）。
- 若将来需要高频实时遥测（秒级功耗、多点温度曲线），只需把 `BatteryCharge` 的计划任务外壳换成常驻 Windows Service 加 Named Pipe 服务端，`Core` 与 `Hardware.MateBook` 不需要重写。
