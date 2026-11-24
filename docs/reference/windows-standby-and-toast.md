# Windows 11 现代待机、事件日志与 Toast 机制参考

- **适用平台**：MateBook 14 2024（`FLMH-XX`），Windows 11（仅支持现代待机 Modern Standby，无传统 S3）
- **验证脚本**：[verify_power_boundaries.py](../../scripts/verify_power_boundaries.py)

---

## 一、 电源键、合盖与睡眠机制边界

以下边界基于插电测试为主、且默认合盖动作为“不采取任何操作”的前置条件测得：

| 机制/接口 | 实测系统行为与边界 | 未测空白 |
|---|---|---|
| **无 S3 睡眠** | 固件不支持 S3（可通过 `powercfg /a` 核验）。在**有 CPU 负载（单核满载）时**，程序调用 `SetSuspendState(睡眠)` 或 `NtInitiatePowerAction(睡眠)` 均直接退化为休眠（`Kernel-Power 42` 目标 S4）；程序模拟睡眠键无反应 | 整机完全空闲时调用 `SetSuspendState(睡眠)` 的行为尚未实测 |
| **高负载下的强制真睡边界** | 在后台高负载时，能够进入现代待机并冻结后台的触发方式为：合盖（合盖动作为睡眠时）、按住电源键（电源键动作为睡眠时）、开始菜单点击“睡眠” | — |
| **电源键物理触发区间** | 轻触（< 1 秒）被华为固件滤掉无反应；按住至屏幕熄灭（约 1–3 秒）触发系统动作；长按约 10 秒触发硬件强制关机。当系统动作设为“关闭显示器”并按住 1–2 秒时，系统直接进入现代待机（`Power Button`）并冻结后台 | — |
| **闲置关屏** | 睡眠设为“从不”时，闲置关屏虽记为进入现代待机（`Idle Timeout`），但后台进程继续运行 | — |
| **闲置睡眠触发条件** | 在合盖动作为“不采取任何操作”时，现代待机下的闲置睡眠必须等待屏幕先关闭，且整机 CPU/负载空闲时才会进入（后台高负载时即使把超时缩至 10–15 秒也无法进入睡眠阶段） | — |
| **合盖外接屏判断** | 当 Windows 合盖动作设为“不采取任何操作”时，合盖后系统不会发送“显示器已关闭”通知；可通过 `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 枚举活动路径，内置屏的 `outputTechnology` 枚举值为 `{0x80000000 (INTERNAL), 6 (LVDS), 11 (DISPLAYPORT_EMBEDDED), 13 (UDI_EMBEDDED)}` | — |
| **电源设置与休眠 API 前置条件** | 通过 `powrprof.dll` 调用 `PowerWriteAC/DCValueIndex` 修改超时后，需调用 `PowerSetActiveScheme` 刷新才会立即生效；普通用户调用 `SetSuspendState(Hibernate)` 前，需先在当前进程令牌上启用 `SeShutdownPrivilege`（普通用户默认持有该权限但处于禁用状态） | — |

---

## 二、 事件日志（睡眠 / 休眠 / 唤醒）判定规则

Windows 系统事件日志可通过 `wevtapi` 或 `wevtutil` 在**普通用户权限（无需管理员）**下读取，内部时间戳为 UTC。

### 1. 关键事件与字段

| 判定目标 | 事件源与 Event ID | 关键字段与判定规则 |
|---|---|---|
| 关屏与现代待机开始 | `Kernel-Power` 506 | 仅表示进入现代待机（通常是关屏），不代表 CPU 已冻结 |
| 真正睡眠时长 | `Kernel-Power` 507 | 仅 `SleepEntered=true` 表示硬件进入低功耗睡眠阶段；`SleepDurationInUs` 为真实睡眠微秒数 |
| 休眠过程与时长 | `Kernel-Power` 42 + `Power-Troubleshooter` 1 | 42（`TargetState=5`）表示准备进入休眠；`Power-Troubleshooter` 1 中 `TargetState=5` 时，`SleepTime` → `WakeTime` 为真实休眠起止时刻（无论时长是否超过 1 分钟均计为休眠） |
| 睡眠/唤醒原因与短暂醒 | 506 / 507 的 `Reason` | `506` 中 `Reason=20`（转休眠/关机）与 `Reason=25`（从休眠恢复）不属于常规进入待机；`507` 中 `Reason=20` 表示即将转入休眠或关机，不属于常规唤醒事件 |
| 日志写入延迟窗口 | `Kernel-Power` 507 | 507 写入事件日志比系统触发开盖消息晚约 1 秒；开盖/亮屏瞬间的即时查询容易漏读该条 507，延后约 15 秒查询并将查询窗口扩展为 `[离开前 5 秒, 开盖后 15 秒]` 可稳定读到 |

### 2. 本机已实测 `Kernel-Power 506/507` `Reason` 代码映射

| `Reason` 代码 | 含义 | 退出待机（唤醒）时的语义 |
|---|---|---|
| `1` | 电源键 | 电源键 |
| `11` | 程序关屏 | — |
| `12` | 闲置超时 | — |
| `15` | 合盖 | 开盖 |
| `20` | 睡眠、休眠或关机 | 排除（不计为短暂唤醒） |
| `25` | 从休眠恢复 | — |
| `28` | 插拔电（屏幕未亮） | — |
| `31` | 键盘 | 键盘 |
| `32` | 鼠标 | 鼠标 |
| `55` | 待机耗电超预算 | — |

---

## 三、 非打包桌面程序 WinRT Toast 通知约束

1. **开始菜单快捷方式约束**：未打包（Unpackaged）桌面程序仅在 `HKCU\Software\Classes\AppUserModelId\<AppID>` 注册而开始菜单中缺少带有同一 `System.AppUserModel.ID` 的 `.lnk` 快捷方式时，系统不会弹出横幅（API 仍返回成功且仅留在通知历史中）。
2. **锁屏期间抑制横幅**：开盖亮屏瞬间系统常处于锁屏界面，此时送达的通知会被系统折叠进通知中心而不弹横幅；会话锁屏与解锁状态变更由 Win32 `WTSRegisterSessionNotification` 推送。
3. **PowerShell 7 兼容性限制**：PowerShell 7（`pwsh`）无法直接加载旧版 WinRT 通知命名空间类型，仅系统内置的 Windows PowerShell 5.1（`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`）支持该脚本桥接方式；直接调用原生 WinRT API 无此桥接限制。
