# 醒来通知：流程与踩过的坑（2026-11-22）

需求 R12：每次醒来弹一条通知，说明刚才是醒着、睡眠还是休眠，多久，为什么；要好看。实现在 `agent/`（常驻）与 `common/evlog.py`、`common/toast.py`。本文时间写本机时间（UTC+8），日志里是 UTC。

## 流程

```
合盖 / 关屏 ──→ 常驻记下“离开”的时刻和电量（brain.went_away）
                    ……睡眠、休眠、短暂醒……（常驻被冻结，什么也不做）
开盖且亮屏 ──→ 离开满 1 分钟才报（brain.came_back → io.report_wake）
                 │ 后台线程，等 15 秒
                 ├─ 读系统日志：Kernel-Power 506/507、42，Power-Troubleshooter 1（common/evlog.query）
                 ├─ 拼成标题和两行字（common/evlog.summarize），并写进 agent/var/agent.log
                 └─ 还在锁屏就先存着，收到“解锁”再发（Agent.show_when_unlocked）
                       └─ Windows PowerShell 5.1 调 WinRT 发通知，发送者 PowerManager（common/toast.py）
```

通知长这样（11-22 的真实数据）：

> **睡了 11 分钟**
> 14:48 合盖 1 分钟 → 15:00 开盖
> 电量 84% → 81%

> **休眠了 1 分钟**
> 14:43 合盖 1 分钟 → 14:47 开盖
> 醒着共 3 分钟 · 合盖 1 分钟，但 2 分钟没睡下去，改为休眠 · 电量 84% → 81%

## 怎么判断睡没睡

| 看什么 | 来源 | 说明 |
|---|---|---|
| 真睡了多久 | 507 的 `SleepEntered`、`SleepDurationInUs` | 506 “进入现代待机”只表示关屏，后台可能还在跑；`SleepEntered=true` 才是进了睡眠阶段 |
| 休眠了多久 | Power-Troubleshooter 1，`TargetState=5`，`SleepTime`→`WakeTime` | 42 是“要进入休眠” |
| 为什么睡、为什么醒 | 506 / 507 的 `Reason` 代码 | 本机见过的代码与中文对照在 `evlog.REASONS`；常驻自己让它睡的，用常驻记下的原因（如“合盖 1 分钟”）代替 |
| 期间短暂醒几次 | 中间的 507 | `Reason=20`（要去休眠或关机）不算醒 |
| 电量 | 常驻在离开和回来时收到的电量通知 | |

## 踩过的坑

| 现象 | 原因 | 改法 |
|---|---|---|
| 通知发出去了（API 返回成功，通知历史里有），但通知中心和横幅都没有 | 没打包的桌面程序，只在 `HKCU\Software\Classes\AppUserModelId\PowerManager` 登记不够；开始菜单里要有一个快捷方式带同一个 AppUserModelID | `common/shortcut.py` 给开始菜单的 `PowerManager.lnk` 写 `System.AppUserModel.ID`；`install.ps1` 每次安装都写 |
| 开盖后通知只进了通知中心，没弹出来 | 开盖亮屏时还在锁屏，锁屏期间到的通知不弹横幅 | 常驻接收会话锁定 / 解锁消息（`WTSRegisterSessionNotification`），锁着就等解锁再发 |
| 明明睡了 10 分钟，通知写“醒着” | 507 比开盖通知晚约 1 秒写进日志，查询截止在开盖那一刻，漏掉了 | 等 15 秒再查，查到开盖后 15 秒 |
| 休眠 55 秒，通知写“醒着”，还说“短暂醒 1 次（睡眠、休眠或关机）” | 休眠只在超过 1 分钟时才算；去休眠前的那条 507（`Reason=20`）被当成了醒 | 有休眠记录就算休眠；`Reason=20` 的 507 不算醒 |
| 合盖后常驻以为接着外接显示器，没让它睡 | 合盖动作是“不采取任何操作”时，合盖后 Windows 不报“屏幕关了” | 改用 `QueryDisplayConfig` 看有没有内置屏以外的显示器在用（`power.external_display_active`） |
| 在 Git Bash 里用 `powershell` 调 WinRT 报“找不到类型” | 本机的钩子把 `powershell` 改写成 `pwsh`（PowerShell 7），PowerShell 7 加载不了 WinRT 类型 | 程序里用完整路径调 Windows PowerShell 5.1（`common.util.POWERSHELL`），不经 shell |

## 怎么核对

- 合盖 3 分钟再开盖、解锁：应弹一条通知；`agent/var/agent.log` 里有一行 `wake: …`，内容与通知相同。
- 只想看通知发不发得出来：`python -c "from common import toast; toast.show('测试', ['一行', '两行'])"`（在项目根目录跑）。
- 通知历史（Windows PowerShell 5.1）：`[Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory('PowerManager').Count`。
