# battery-charge：电池充电上限 80% 保护与临时冲高

不装华为电脑管家，直接经系统自带的 ACPI WMI 接口（`root\wmi` 下 `OemWMIMethod.OemWMIfun`）把主板 EC 充电阈值锁在日常 **75%–80%**，并可发起“单次临时冲高”（如出门前充到 90%、95% 或 100%），到期或拔电后自动回落到 75%–80%。界面在 `../ui/`（`PowerManager.lnk` 的“充电”一栏），不弹 UAC。

## 用法

```powershell
$T = "$env:USERPROFILE\Desktop\PowerManager\battery-charge"
python "$T\battery_charge.py" --status  # 命令行查看当前状态
python "$T\battery_charge.py" --boost 95 --hold-min 60 --max-hours 4
```

计划任务由项目根目录的 `install.ps1`（管理员）注册。

- **界面**：可调三项参数——**临时冲高到 (%)**（85–100，也有 90% / 95% / 100% 快捷按钮）、**到了插着电保持 (分钟)**（默认 60 分钟）、**最长 (小时)**（默认 4 小时）。
- **不留永久 100% 入口**（用户 2026-11-08 定）：界面与命令行都只能发起带自动回落的临时冲高，也不设手动恢复按钮；日常基准 `DAILY_START = 75`、`DAILY_STOP = 80` 写死在 `battery_charge.py` 源码里。

## 它改什么

计划任务 `Battery-ChargeLimit`（最高权限，`pythonw.exe` 无窗口，**不管有没有登录都运行**，每次跑一遍 `battery_charge.py --task` 就退出）。没有定时触发，有事才跑（用户 2026-11-22 定：不轮询）：开机、登录、醒来（Power-Troubleshooter 1）、插拔电（Kernel-Power 105）；另由常驻（`../agent/`）在电量每变 5%、冲高到点、通勤途中每 3 分钟查温度时启动，原因写在 `var/kick.json`，记进 `history.log` 的 `why=`。

1. 平时保持 EC 充电阈值为 `75%–80%`（高于 80% 不充，低于 75% 起充）：每次先读阈值，和应有的不一致才写，写了记进 `var/ec.log`；
2. 当界面或 `--boost` 写入 `var/request.json` 并触发任务时，立即将 EC 设为 `(上限-5)%–上限%`；
3. 临时冲高期间，满足以下**任一条件**即自动把 EC 改回 `75%–80%`：
   - 电量达到目标上限且插着电累计满设定分钟数（界面可调，默认 60 分钟）；
   - 电量已达标后拔掉电源；没达标时拔电满 10 分钟（常驻到点叫任务）；
   - 距离点击开始已超过最长兜底时间（界面可调，默认 4 小时）。

只写充电阈值这一条指令（0x10）。电脑管家的“恢复充电”（0x12）和“充电模式”（0x15）只读不写；EC 在发热或整机功耗高时自己压低充电电流，这是保护，本工具不碰（用户 2026-11-11 定）。

## 核实

```powershell
Get-ScheduledTask Battery-ChargeLimit | Select-Object TaskName, State
python "$env:USERPROFILE\Desktop\PowerManager\battery-charge\battery_charge.py" --status
```

输出中 `充电阈值` 为 `75%–80%`（或当前正在生效的临时冲高区间）。读数时间不再说明任务在不在跑：现在有事才读，几小时不读是正常的（插着电、电量不变时）。

是否真的生效看电池的实际行为，不看指令返回值：插着电到 80% 时 `var/history.log` 里 `chg_mW` 归零、`cap_mWh` 不再上涨。

<!-- 以下按需读 -->

## 原理

MateBook 14 2024（`FlemingH`）的电池充电控制通过主板 ACPI WMI 设备（`PNP0C14`，`root\wmi` 命名空间的 `OemWMIMethod.OemWMIfun`，输入 64 字节 `u8Input`）与主板 EC 通信。反编译、实测与电脑管家的用法记录在 `../notes/battery-wmi-decompile-20261111.md`，结论：

| 输入 | 是什么 | 本工具 |
|---|---|---|
| `03 10 start stop` | 设充电阈值（EC `0x0268`），与 Linux `huawei-wmi` 的 `BATTERY_THRESH_SET` 相同；实测换用它以后电池停在 80% | 写 |
| `03 11` | 读充电阈值，输出 `[状态, start, stop]` | 读 |
| `03 12 a` | 电脑管家的“恢复充电”：智能充电控住电量后，让 EC 在这次插电期间放开 | 不写（例行写 1 等于反复按“恢复充电”） |
| `03 15 …` / `03 16` | 电脑管家的充电模式（智能 / 自定义）；单独写限不住 80% | 只读 `03 16` |
| `02 0D 00` / `02 05 00` / `02 02 n` / `02 0C 15` | 充电芯片充电电流、整机功耗、各温度传感器、电池温度 | 读，显示在状态里 |

充得慢时，看状态里的充电电流：EC 在 CPU 热、整机功耗高时把充电电流压到 448 mA 左右，凉下来逐档放到 960、1984 mA（2026-11-11 实测），与阈值无关。

因为 `root\wmi` 需要管理员权限才能调用，由计划任务 `Battery-ChargeLimit` 以最高权限执行 WMI 读写（`../common/ec.py`，经 `comtypes` 直接调 WMI，2026-11-22 起不再起 PowerShell；每条 EC 指令约 80 ms，全部读一遍约 1.5 秒）；普通权限的界面写 `var/request.json` 后调用 `schtasks /Run /TN Battery-ChargeLimit` 即刻触发，无需 UAC 弹窗。

重启后 EC 阈值会变成 95-100（2026-11-11 实测：17:09 Windows 更新自动重启两次，之后停在登录界面，20:43 登录时读到 95-100，电量已充到 100%）。所以任务的登录方式是 S4U（“不管用户是否登录都运行”、不存密码），并在开机时触发。

不接入自适应充电算法（2026-11-10 登记为待办，2026-11-11 结案，Claude 判断）：它每个周期算出一个充电电流（恒流、降流、恒压，带温度降额），这台机器的 WMI 只能设启充、停充百分比，没有设电流的接口，恒流恒压也已由电池包和充电芯片在硬件上做了；它的默认参数（单节 4.20 V、4.5 Ah）也不合这块 3 串、每串 4.48 V、约 70 Wh 的电池。能落到本机的“到某电量停、过热时停”，EC 阈值与 EC 自己的发热降档已经在做。

策略（何时冲高、何时回落）是纯函数 `decide()`，测试在 `test_battery_charge.py`：在本目录跑 `python -m unittest test_battery_charge`。

## 数据

`var/` 不进 git；2026-11-22 移出 Config 以后，也不再由 Config 的每日备份（`cfg.py backup`）复制。标“缓存”的能重新生成，可以删；其余要保留。路径相对于本文件所在的目录。

| 路径 | 类 | 是什么 |
|---|---|---|
| `var/state.json`、`var/request.json`、`var/kick.json` | 缓存 | 当前 EC 读数与临时冲高计时；界面瞬时请求；常驻启动任务的原因 |
| `var/ec.log` | 保留 | 每次写 EC、冲高开始与回落、出错，各一行（UTC） |
| `var/history.log`（`.1`） | 保留 | 每次运行一行：电量、交直流、阈值、充放电功率、容量、充电电流、整机功耗、温度、为什么运行（`why=`）（UTC）；超过 1 MB 轮换一次。2026-11-22 以前是每 5 分钟一行 |
| `var/error.log` | 保留 | 出错时的完整 traceback |
