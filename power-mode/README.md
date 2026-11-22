# power-mode：普通 / 保持运行 两种电源模式

手动切换。打开桌面或开始菜单的 `Power Mode`，或：

```powershell
D:\miniforge3\python.exe "$env:USERPROFILE\Desktop\PowerManager\power-mode\power_mode.py" --mode run --commute-min 20
D:\miniforge3\python.exe "$env:USERPROFILE\Desktop\PowerManager\power-mode\power_mode.py" --mode normal
D:\miniforge3\python.exe "$env:USERPROFILE\Desktop\PowerManager\power-mode\power_mode.py" --status
```

## 两种模式

| | 普通 | 保持运行（跑实验） |
|---|---|---|
| 插电：闲置睡眠 / 关屏 | 5 小时 / 5 小时 | 从不 / 10 分钟 |
| 电池：闲置睡眠 / 关屏 | 1 小时 / 1 小时 | 通勤分钟数（默认 20）/ 5 分钟 |
| 合盖、电源键 | 睡眠 | 什么都不做 |
| 后台守护（只在电池上） | 不管 | 见下 |

普通模式的闲置时间是用户 2026-11-21 在设置里选的值。

保持运行模式在电池上（通勤）时，没有输入满通勤分钟数，由 Windows 睡眠（现代待机，约 0.5 W）：实验暂停，开盖后继续；网络连接会断。后台守护每 30 秒看一次，遇到下面任一情况就**休眠**（写盘断电，实验的进程原样保留）：

- 电量 ≤ 10%；
- 电池温度 ≥ 45 °C，且 1 分钟没人操作（有人在用时只弹通知）；温度取自 `../battery-charge/var/state.json`，在电池上时每 2 分钟让 `Battery-ChargeLimit` 任务刷新一次；
- 没有输入已超过通勤分钟数 + 10 分钟还醒着（有程序挡住了睡眠），且没有程序要求屏幕常亮（视频、放映）。

## 它改什么

- 当前电源计划的四项，插电、电池各一份：`STANDBYIDLE`、`VIDEOIDLE`、`LIDACTION`、`PBUTTONACTION`（`powercfg /setacvalueindex`、`/setdcvalueindex`，不需要管理员）。在系统设置里手动改过以后，`--status` 会说与模式不一致，再点一次模式按钮就恢复。
- 第一次切换前的四项存在 `var/original.json`；`--restore-original` 放回去（同时把模式记成普通）。
- 计划任务 `PowerManager-ModeWatch`：登录时以当前用户运行 `power_mode.py --watch`（`install_watch.ps1` 注册，不需要管理员，`-Uninstall` 卸掉）。普通模式下它什么都不做。
- 快捷方式：桌面与开始菜单 `Power Mode.lnk`（图标借 `../battery-charge/icon.ico`）。

不改：休眠本身（开着，危急电量 2% 时休眠，是系统原来的设置）、低电量动作、快速启动（关着）。

## 数据

| 路径 | 类 | 是什么 |
|---|---|---|
| `var/mode.json` | 保留 | 当前模式与通勤分钟数 |
| `var/original.json` | 保留 | 第一次切换前的电源设置 |
| `var/mode.log` | 保留 | 切换、休眠、警告，各一行（UTC） |
| `var/error.log` | 保留 | 守护出错的 traceback |

策略是纯函数 `decide()`，测试：在本目录跑 `D:\miniforge3\python.exe -m unittest test_power_mode`。
