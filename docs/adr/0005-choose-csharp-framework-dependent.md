<!--
类别：决策状态。决策被推翻时原地改写本文，被推翻的方案及原因写进"备选方案"。
-->

# ADR-0005: 用 C#（.NET 10），以 Framework-Dependent 方式发布

## 背景

驱动力：

- 核心功能重度依赖 Windows 专有接口：`root\wmi:OemWMIMethod`、Win32 消息循环、`powrprof.dll`、`wevtapi`、WinRT Toast、WebView2。
- 目标机器已装有 .NET 10 运行时与 WindowsDesktop 运行时。
- 开发机的原生工具链是 MSYS2 UCRT64，没有安装 MSVC / Visual Studio Build Tools（数 GB）。

## 决策

主程序用 **C#（.NET 10 SDK）** 编写，以 **Framework-Dependent** 方式发布（可开启 ReadyToRun）；不启用 Native AOT，不用 Rust。

## 备选方案

1. **Python**（v1 的实现语言）：没有编译期类型检查，运行时开销更高；C# 对上述 Windows 接口都有第一方支持。
2. **Native AOT**：在 Windows 上硬性依赖 MSVC `link.exe` 与 Windows SDK，不能复用 MSYS2 工具链；Framework-Dependent 只需约 200 MB 的 .NET 10 SDK，也没有裁剪限制。
3. **Rust**：调用 WMI `ExecMethod`（`VARIANT` / `SAFEARRAY`）与 WebView2 回调需要大量 `unsafe` 代码；`x86_64-pc-windows-gnu` 目标与部分 Windows 生态库存在链接兼容风险；换来的约 20 MB 常驻内存差异在使用中感知不到。
4. **Self-Contained 发布**：目标机已有运行时，自带运行时只增加体积。

## 影响

- .NET 工具链与 MSYS2 原生工具链互不依赖。
- 部署依赖目标机已安装 .NET 10 Desktop Runtime。
- 若将来要在没有 .NET 运行时的机器上免安装运行，补装 MSVC Build Tools 并调整工程配置，即可改为 Native AOT 或 Self-Contained 发布，不需要重写业务逻辑。
