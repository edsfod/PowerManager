<!--
类别：状态。读者：用户（将来的自己、同款机型的人）。
装：这是什么、适用条件、安装、使用、升级与卸载、常见问题、文档导航。
不装：代码结构与测试（→ ARCHITECTURE.md）、精确时长与阈值（→ docs/requirements.md，这里只概括）。
-->

# PowerManager

华为 MateBook 14 2024（`FLMH-XX`）在 Windows 11 上卸载华为电脑管家后，用来接管电源与电池管理的小工具。它要同时做到三件事：

- **长时间任务不中断**：需要时，关屏、合盖都不打断后台任务；
- **放进包里不发热、不耗电**：合盖后让电脑睡下，睡不下就休眠；
- **电池不长期满充**：日常充电上限锁定在 `75%–80%`，需要时可以临时多充一次。

**适用范围**：只支持 Windows 11 与这一款机型。不支持 Linux、macOS、鸿蒙，不兼容其他品牌笔记本，不提供永久 100% 充电开关。

---

## 安装

**运行环境**：Windows 11（x64）、[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）、Microsoft Edge WebView2 Runtime（Windows 11 自带）。

**方式一：Release 压缩包**

1. 从 [Releases](https://github.com/edsfod/PowerManager/releases) 下载 `PowerManager-vX-win-x64.zip`，解压到一个长期保留的目录（计划任务直接指向其中的 `dist\`）。
2. 在解压目录用**管理员** PowerShell 运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

**方式二：从源码构建**（另需 .NET 10 SDK）

1. 在仓库根目录运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```

2. 用**管理员** PowerShell 运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

`install.ps1` 会注册两个计划任务（常驻调度 `PowerManager-Agent`、提权写充电阈值的 `Battery-ChargeLimit`），登记通知标识，并在桌面与开始菜单创建 `PowerManager` 快捷方式。

补充说明：

- 运行时状态与日志在 `%LOCALAPPDATA%\PowerManager`；`pmctl paths` 列出各文件位置。
- 计划任务与快捷方式指向 `dist\` 的绝对路径，**移动目录后需重新运行 `install.ps1`**。
- 从 v1 迁移时，加 `-ImportOriginalFrom <v1 的 original.json>`，只读导入原始电源设置。

---

## 使用

从桌面或开始菜单打开 **PowerManager**。

### 两种模式

| | 普通（默认） | 保持运行（跑任务、通勤） |
|---|---|---|
| 合盖 | 没接外接显示器时，短暂延迟后睡眠 | 继续运行 |
| 插电闲置 | 先关屏，再睡眠 | 只关屏，不自动睡眠 |
| 用电池闲置 | 先关屏，再睡眠 | 先关屏，闲置更久才睡眠；电池过热时提前睡眠 |
| 电源键 | 禁用（只保留待机时按压唤醒、长按约 10 秒强制关机） | 同左 |

- “保持运行”可以定时，到期自动回到“普通”；也可以选“直到手动改回”。
- 调整时长或冲高参数后，要点“确认”才生效。
- 各项具体时长、温度阈值、电源模式切换点见 [docs/requirements.md](docs/requirements.md)（`SLP-*`、`PWR-*`）。

### 睡不下时自动休眠

程序主动让电脑睡眠后（例如普通模式合盖），如果后台负载高、迟迟睡不下，等待一段时间后改为休眠（`SLP-10`）。合盖放着时电脑自己醒了、一直不睡回去，也会这样处理（“保持运行 + 插电”除外，`SLP-11`）。后台高负载时程序没办法让系统进入睡眠，只能休眠；这时想要真正的睡眠，请在开始菜单点“睡眠”。

离开一段时间后回来解锁，会弹出一条通知（`NTF-01`），写明这段时间电脑是睡眠、休眠还是一直醒着，以及电量变化。

### 临时冲高

出门前需要更多电量时，在界面里把充电上限临时调高（最高 100%）。充满后插着电保持一段时间、充满后拔电、没充满就拔电较长时间，或超过最长时限，任一条件满足即自动回到 `75%–80%`。参数范围与回落条件见 `CHG-02`。

### 命令行 `pmctl`

`pmctl` 以普通用户权限运行，不弹 UAC：

| 命令 | 作用 |
|---|---|
| `pmctl status` | 当前模式、电源设置、常驻状态、充电阈值 |
| `pmctl mode normal`<br>`pmctl mode keep-running --hours 12` | 切换模式（`--hours 0` 表示直到手动改回） |
| `pmctl boost 95 --hold-min 60 --max-hours 4` | 发起临时冲高 |
| `pmctl restore-original` | 恢复 Windows 原始电源设置并让常驻退出（**下次登录会重新启动**，长期停用见“暂停”） |
| `pmctl paths` | 列出数据目录与各文件路径 |

插电且电量不变时，`pmctl status` 显示的 EC 读数时间可能几小时不更新，这是正常的：硬件任务只在事件发生时运行。管理员终端也可以直接运行 `PowerManager.BatteryCharge.exe --status` / `--boost <目标>`。

---

## 升级、暂停与卸载

以下命令都在**管理员** PowerShell 中执行。

**升级**：常驻与界面运行时会锁住 `dist\` 里的文件，要先停掉。

1. 关闭 PowerManager 窗口，停止常驻：

   ```powershell
   Stop-ScheduledTask -TaskName PowerManager-Agent; Stop-Process -Name PowerManager.Agent -ErrorAction SilentlyContinue
   ```

2. 替换 `dist\`：用新压缩包里的 `dist\` 覆盖；从源码构建的，在仓库根目录运行 `scripts\publish.ps1`（`dist\` 中有程序在运行时它会直接报错退出）。
3. 运行 `install.ps1`，重新注册并启动计划任务。已有的 `original.json` 与日志会保留。

**暂停**（临时交还 Windows 管理电源设置）：

```powershell
Disable-ScheduledTask -TaskName PowerManager-Agent; pmctl restore-original
```

恢复：

```powershell
Enable-ScheduledTask -TaskName PowerManager-Agent; Start-ScheduledTask -TaskName PowerManager-Agent
```

暂停期间，`Battery-ChargeLimit` 仍会在开机、唤醒、插拔电时维持 `75%–80%` 上限。

**卸载**：先运行 `pmctl restore-original` 恢复原始电源设置，再运行 `install.ps1 -Uninstall`，移除计划任务、快捷方式与通知标识。`%LOCALAPPDATA%\PowerManager` 中的数据会保留。

---

## 常见问题

| 现象 | 处理 |
|---|---|
| 界面顶栏显示红点“常驻没在运行” | 点旁边的“启动”。仍然离线时，查看 `agent/error.log`，以及任务计划程序中 `PowerManager-Agent` 的上次运行结果 |
| 点“刷新读数”后 EC 更新时间不变 | 查看任务计划程序中 `Battery-ChargeLimit` 的上次运行结果：`0x2` 表示写阈值后读回不一致（`battery-charge/ec.log` 中有 `MISMATCH`）；`0x1` 表示其他异常（见 `battery-charge/error.log`） |
| 醒来通知没有弹横幅 | 锁屏期间的通知会推迟到解锁后。一直不弹时，检查开始菜单里是否有 `PowerManager` 快捷方式（横幅依赖它携带的通知标识），没有就重新运行 `install.ps1` |
| 移动目录后快捷方式或计划任务失效 | 重新运行 `install.ps1` |

---

## 文档导航

| 文档 | 回答 |
|---|---|
| [docs/requirements.md](docs/requirements.md) | 系统应该表现成什么样：目标、场景、每条需求的规格与验收 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 代码在哪、什么不能破坏、改一处要连带改哪里、怎么测试 |
| [docs/design.md](docs/design.md) | 调度、睡眠兜底、充电决策怎么运作；进程间接口与状态文件长什么样 |
| [docs/ui-design.md](docs/ui-design.md) | 界面布局、窗口行为、草稿确认 |
| [docs/adr/](docs/adr/) | 为什么选了现在的方案 |
| [docs/reference/](docs/reference/) | 本机硬件 WMI / EC 接口与 Windows 待机、通知行为的实测事实 |
| [docs/guides/porting-guide.md](docs/guides/porting-guide.md) | 换一台 Windows 笔记本时怎么移植 |
| [docs/roadmap.md](docs/roadmap.md) | 远期可能做的方向与前提条件 |
| [CHANGELOG.md](CHANGELOG.md) | 每个版本改了什么 |
