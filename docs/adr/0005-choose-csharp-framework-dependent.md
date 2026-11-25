# ADR-0005: 选用 C#（.NET 10 SDK + Framework-Dependent）作为重写技术栈

- **状态**：Accepted
- **日期**：2026-11-24

## 背景
在完成文档与接口验证基线重构后，需要为 `PowerManager-v2` 选定代码实现语言与分发构建方式。本机已有环境约束如下：
- 系统已预装 `.NET 8 / 9 / 10` 运行时与 `WindowsDesktop` 运行时；
- 原生 C/C++ 工具链采用轻量级 MSYS2 UCRT64，未安装数 GB 的 MSVC / Visual Studio Build Tools；
- 核心业务重度依赖 Windows 专有接口（`root\wmi:OemWMIMethod`、Win32 消息循环、`powrprof.dll`、`wevtapi`、WinRT Toast 与 WebView2）。

## 决策
选用 **C#（.NET 10 SDK）** 重写主程序，采用 **Framework-Dependent（可开启 ReadyToRun）** 方式发布，不启用 Native AOT（`PublishAot=true`），不改用 Rust。

## 依据
1. **Windows 生态契约平滑**：C# 对 WMI（`System.Management` / COM）、Win32 P/Invoke、WinRT Toast 与 `Microsoft.Web.WebView2` 均为第一方原生支持；相比 Python 可获得编译期静态类型检查与更低的运行时开销。
2. **避免引入庞大的 MSVC 构建链**：Native AOT 在 Windows 上硬性依赖 MSVC `link.exe` 与 Windows SDK，无法复用本机已有的 MSYS2 UCRT64；而本机已装有 `.NET 10` 运行时，Framework-Dependent 模式仅需安装约 200 MB 的 `.NET 10 SDK`，且无裁剪（Trimming）限制。
3. **优于 Rust 的工程性价比**：Rust 在调用 WMI `ExecMethod`（`VARIANT` / `SAFEARRAY`）及 WebView2 回调时需编写大量 `unsafe` 代码，且 `x86_64-pc-windows-gnu` 目标在部分 Windows 生态库上存在链接兼容风险；换取的仅约 20 MB 常驻内存差异在实际使用中无感知价值。

## 影响
- 新增独立 `.NET 10` 开发工具链，与既有 MSYS2 原生工具链互不依赖。
- 后续部署包依赖目标机器已安装的 `.NET 10 Desktop Runtime`（本机已具备）。
- 若未来确需在无 `.NET` 运行时的全新 Windows 机器上单文件免环境运行，只需补装 MSVC Build Tools 并调整工程配置即可切换至 Native AOT 或自包含（Self-Contained）发布，无需重写业务逻辑。
