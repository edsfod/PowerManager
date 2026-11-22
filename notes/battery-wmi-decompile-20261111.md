# 电池充电 WMI 接口反编译与实测（2026-11-11）

依据：MateBook 14 2024（型号 `FLMH-XX`），BIOS 1.17（2025-04-22）；ACPI 表 `SSDT` OEM Table ID `WmiTable`、OEM Revision `0x00001000`；`battery_charge.py` 为 Config 仓库提交 `cb9a5ba` 的版本（文中的提交号都在 Config 仓库里：本工具 2026-11-22 移出 Config 前的历史在那里）。时间写本机时间（UTC+8）。

起因：电池 13:40 插电时 25%，到 14:50 才 39%，充电功率 2–5 W。`battery_charge.py` 用的 0x12/0x15 两条指令此前在仓库里没有出处，只有 README 一句“反编译确认”。

## 怎么拿到的

- ACPI 表：注册表 `HKLM\HARDWARE\ACPI` 下有 DSDT 与 SSD1…SSDT 共 31 张，不要管理员就能读，按值原样写成 `.aml`。
- 反编译：conda-forge 的 `acpica-tools`（Windows 版，装进临时环境）里的 `iasl -e DSDT.aml -d WmiTable.aml`。
- 调用：`root\wmi` 的 `OemWMIMethod.OemWMIfun`（要管理员，用 `Start-Process pwsh -Verb RunAs`），输入 64 字节 `u8Input`，输出 `u8Output`。

## 调度

WMI 设备 `\_SB.HWMI`（`PNP0C14`）的 `WMAA` 取 `u8Input[0]` 为主功能号、`u8Input[1]` 为子功能号，分派到各方法。与充电有关的：

| 输入 | 方法 | 做什么（ASL 源码） |
|---|---|---|
| `02 0C rid` | GBAI | 读电池信息：rid 1 充电电流、2 充电电压、3 电池状态、0x0B SOH、0x0C 安全状态、0x0D 永久故障状态、0x0F 充电状态、0x15 NTC 温度 等（经 `EC80`，即 EC 命令 `0x0280`） |
| `02 0D rid` | GCII | 读充电芯片：rid 0 充电电流 mA、1 充电电压 mV、4 PROCHOT、5 充电状态、6 输入电流 mA、7 输入电压 mV、8 最大充电功率（EC `0x0263`） |
| `02 0E rid` | GADI | 读适配器：rid 0 协议、1 最大功率、2 线缆信息（EC `0x0263`） |
| `02 05 rid` | GPCI | 读功耗：rid 0 整机、1 CPU（EC `0x025B`） |
| `03 10 s e` | SBTT | 写 EC `0x0268`：先写子项 3 = 90（`DAT1=3, DAT2=0x5A`），再写子项 1 = 启充 `s`、子项 2 = 停充 `e`；要求 `0 ≤ s ≤ e ≤ 100`；`e = 0` 时只写启充 |
| `03 11` | GBTT | 读 EC `0x0269` 子项 1、2，输出 `[状态, 启充, 停充]`；读到 0xFF 算失败 |
| `03 12 a` | SBAC | `a = 1` 往 EC `0x02E5` 写 1，`a = 2` 写 0，别的值不写；调试串叫“battery active charge flag” |
| `03 13` | GBAC | 读 EC `0x02E6`，读到 0 时改报 2 |
| `03 14` | GAIT | 读 EC `0x02E2`，“AC-IN Time” |
| `03 15 b0 b1 b2 b3` | SBCM | 把输入第 2–5 字节原样写给 EC `0x02E3`，调试串叫“battery charge Mode” |
| `03 16` | GBCM | 读 EC `0x02E4` |
| `03 18 …` | STSP | 写 DPTF 变量 `ODV7/8/9` 并通知 `IETM`（温控，不是充电） |
| `04 …`（SSCV） | SSCV | 调试串“Set full charge voltage”（EC `0x03BF`）；没试 |

0x10/0x11 与 Linux 内核 `drivers/platform/x86/huawei-wmi.c` 的 `BATTERY_THRESH_SET 0x1003` / `BATTERY_THRESH_GET 0x1103`（小端即 `03 10` / `03 11`，参数在第 2、3 字节）一致。0x12、0x15 在 ACPI 里只是把字节转给 EC，EC 固件怎样用 `0x02E5`、`0x02E3` 在 ACPI 层看不到。

## 实测

14:59 只读（`u8Output[1..2]` 按小端 16 位读）：

| 项 | 读数 |
|---|---|
| GBTT | `0,75,80` |
| GBAC | `1` |
| GBCM | `0,4,1,75,80` |
| 适配器最大功率 / 最大充电功率 | 65 W / 65 W |
| 电池请求的充电电流 / 电压 | 12000 mA / 13440 mV（3 串，每串 4.48 V） |
| 电池状态 / 安全状态 / 永久故障 | `0x0080` / 0 / 0 |
| NTC 温度 | 3096（0.1 K）= 36.5 °C |
| 充电芯片充电电流 | 448 mA（× 11.6 V ≈ 5.2 W，与 Windows `BatteryStatus.ChargeRate` 5.3 W 一致） |
| 充电芯片输入电流 | 3150 mA |
| 充电芯片输入电压 | 原始 `0,0,5`，单位不明 |

15:03 切 0x12（提权脚本，写完各等 3 s、7 s 读）：

| 时刻 | 动作 | 整机功耗 | 充电电流 | 输入电流 | GBAC | Windows 充电功率 |
|---|---|---|---|---|---|---|
| 15:03:26 | 之前 | 59 W | 448 mA | 3150 mA | 1 | 3384 mW |
| 15:03:30 | SBAC(2) 后 3 s | 60 W | 448 | 3150 | 1 | 4691 |
| 15:03:34 | SBAC(2) 后 7 s | 43 W | 448 | 3150 | 1 | 4691 |
| 15:03:37 | SBAC(1) 后 3 s | 57 W | 448 | 3150 | 1 | 4691 |
| 15:03:42 | SBAC(1) 后 7 s | 52 W | 448 | 3150 | 1 | 4784 |

两次写入都返回状态 0（成功），但 GBAC 一直是 1，Windows 报的充电功率也没有降到 0。CPU 功耗一项读到 234，不像瓦数，没采用。GCII 的两个电流 15 s 内一点没变，可能是充电芯片的设定值而不是实时测量值。

## 历史对照（`powercfg /batteryreport`）

- 提交 `fe2c1f8`（11-10 11:35）到 `cb9a5ba`（12:19）之间，代码每 5 分钟只写 0x15（`4,1,75,80`）、读 0x16。11:36 插电时 72%，12:07 拔电时 89%，越过了 80%。当时有没有临时冲高在跑，没留状态，排除不了。
- 换成 0x10 以后，11-10 插电的三段（13:44、17:20、19:14 起）都停在 80%（16:47、17:55、20:53 拔电时 80%）。

## 结论

- **慢的原因**：整机功耗 43–60 W，适配器 65 W，留给电池的只有约 5 W。EC 阈值（75/80）、电池状态、温度都没有限制充电。不是 `battery_charge.py` 造成的。
- **0x10/0x11**：真正管充电上限的就是这一对，有 Linux 驱动对照，也有实测（换上后停在 80%）。
- **0x12（SBAC）**：ACPI 层只是写 EC `0x02E5`。实测写 0 以后 7 s 内充电没断，`0x02E6` 也没变，README 原来说的“立即强制切断”不成立。它在 EC 里的作用不明（可能只在插拔电等时刻才被 EC 读取），就本机现有证据看没有可见效果。
- **0x15（SBCM）**：ACPI 层只是把 4 个字节存进 EC `0x02E3`，读回的是写进去的值。只写 0x15 的那段时间电池越过了 80%，说明它单独不能限充；更像华为电脑管家给自己记的“模式”。

## 更正（同日 15:50 以后的实测）

上面“慢的原因”一条错了：那是由“整机 + 电池 ≈ 65 W”推出来的，没有实测。实测如下：

- 15:50 每 3 s 采一次：整机 44–62 W 之间跳，电池一直约 1.8 W；整机降到 44 W 时电池并没有多充，所以不是适配器被吃满。
- 15:52 关掉两个空转、各占满一个核的 Claude Code 进程以后，EC 把充电芯片的充电电流设定值（GCII rid 0）一档档往上调：448 mA（7 × 64）→ 15:53:15 960 mA（15 × 64）→ 15:57:23 1984 mA（31 × 64）。电池功率随之 5.2 W → 11.6 W → 23.7 W；同时 GTMP 传感器 0（读数最高的一路，像 CPU）从 72 °C 降到 52 °C，电池 NTC 35.9 → 35.1 °C，整机功耗多在 14–25 W。适配器输入电压（GCVA rid 0）读到 20000，即 20 V。
- DPTF 表里有充电器参与者 `\_SB.IETM.CHRG`（档位 3500/3000/…/500/0 mA），但它的 `SPPC` 要调 `\_SB.PC00.LPCB.H_EC.DPTF.SCHG`，本机 EC 设备叫 `HWEC`，没有 `H_EC`，所以 Intel DTT 管不了充电电流；本机 `ipfsvc`、`dptftcs` 两个服务是 Disabled，与此无关。

结论：充电慢是 EC 固件自己按发热（或整机功耗）压低了充电电流，机器凉下来就逐档放开。这是保护：CPU 发热与充电发热不叠加（用户 2026-11-11 认为是好事，要保留）。与 `battery_charge.py` 无关。

## 电脑管家怎样用这几条（同日，只看导出名与字符串，未反汇编）

来源：华为官网 `consumer.huawei.com/cn/support/pc-manager/` 的“立即下载”，`PCManager_Setup_14.0.7.310(C233)_x64.exe`，Authenticode 有效、签名者 Huawei Device Co., Ltd.；只解包，未安装。看的是 `WmiUtil.dll`、`plugins\SmartChargePlugin.dll`、`res\layout\CustomChargeCapacity.xml`、`res\values\zh_CN.res`。

- `WmiUtil.dll`（`HwSDK::BiosWmi`）导出的充电函数，与 ACPI 方法名一一对应（按名字对应，未反汇编核对字节）：`SetBatteryChargeThreshlod(u8, u8)` ↔ SBTT 0x10；`SetSmartChargeMode(SmartChargeMode, bool, int, int, int)` / `GetSmartChargeMode` ↔ SBCM 0x15 / GBCM 0x16；`SetResumeChargingFlag(bool)` / `GetResumeChargingFlag` ↔ SBAC 0x12 / GBAC 0x13；`GetAcDurationTime` ↔ GAIT 0x14（“AC-IN Time”）。
- `SmartChargePlugin.dll` 的日志串：`set resumeFlag of bios:`、`ClickResumeCharging`、`AcResumeTips, in one ac duration`、`get bios/ec charge mode=`、`set as old smart mode, mode=`；界面串键名 `IDS_SMART_CHARGE_RESUME_CHARGING`、`IDS_STOP_CHARGE_DESC`；注册表 `HKLM\SOFTWARE\Huawei\PCManager\setting` 下 `SmartChargeMode`、`CustomChargeCapacity`（本机已无此键）。
- 界面文字（zh_CN）：
  - “智能充电模式”：“开启智能充电模式，在连接电源适配器过久或电池温度过高时，将电池电量控制为 {0}”；“为延长电池寿命，电量将自动控制到 {0}。如需继续充电，请点击‘恢复充电’”；“为延长电池寿命，已对电池电量进行限制。温度恢复后，可继续充电。”
  - “自定义充电模式”：“您可自行设置最大充电电量”，菜单 50/60/70/80/90/100（`CustomChargeCapacity.xml`）；“电量高于 {0}% 时暂停充电”。

由此：

- **0x12 是“恢复充电”**：智能充电把电量控住以后，用户点“恢复充电”，管家写这个标志让 EC 在这一次插电期间放开限制（`in one ac duration`）。15:03 在 39% 切它没有反应，是因为当时本来就没有限制在生效。`battery_charge.py` 在电量 ≤ 75% 时每 5 分钟写一次 1，等于反复按“恢复充电”，可能盖掉 EC 的智能保护，应去掉。
- **0x15 是充电模式**：在“智能”（EC 按插电时长、电池温度自己控电量）与“自定义”（固定上限）之间选。`4` 是哪种模式，只有 11-08 那次对 `WmiUtil.dll` 的分析说是“自定义”，这次没核对枚举值。实测它单独写时限不住 80%，限充靠 0x10。

## 重构后实测 0x10（同日 17:04，`battery_charge.py` 提交 `76cb22f`）

插着电、电量 59%、充电约 20.7 W（充电电流 1984 mA）时，提权写 `03 10 40 45`（返回状态 0，GBTT 读回 40-45）：3 s 内充电芯片充电电流降到 0、Windows `ChargeRate` 为 0、`Charging` 为 False，之后 30 s 一直如此；这段时间电池在放电（任务 17:05:14 记下 `dis_mW=11658`，容量 41801 → 41568 mWh），即电量高于停充值时插着电也由电池供电。17:05:14 触发计划任务：它读到 40-45 与应有的 75-80 不符，写回并记进 `var/ec.log`（`set 75-80 (was 40-45, battery 59%, AC) -> readback 75-80`），17:05:31 起 `ChargeRate` 回到 23.7 W。

## 重启后阈值变成 95-100（同日 21:00 发现）

用户 21:00 左右看到电量 90%。`var/history.log` 在 17:08（UTC 09:08）之后断了 3.5 小时，下一行是 20:43:51，那时电量 100%，`var/ec.log` 记着 `set 75-80 (was 95-100, battery 100%, AC)`。系统日志：17:09 Windows 更新（KB5132061）自动重启，17:12 又重启一次，17:13 开机后停在登录界面，20:43 才登录。计划任务当时是 InteractiveToken（只在用户登录后运行），登录前一次都没跑，EC 按 95-100 充满了。登录后任务改回 75-80，插着电从 100% 放电，21:06 到 90%。

- 95-100 是谁写的没查：可能是 EC 或 BIOS 重启时的默认值，也可能是开机时别的东西写的。本机还在运行的华为服务只有触控板（HuaweiThpService）、显示（HWDisplayService）、音效（HWVEAudioService）。
- 改法：任务改为 S4U（不管是否登录都运行，不存密码），加开机触发和睡眠唤醒触发（Power-Troubleshooter 事件 1）；EC 脚本改用 `powershell.exe` 5.1，因为应用商店版 `pwsh` 是登录用户会话里的应用别名，没登录时调不到。21:09 起新任务运行正常，读回 75-80。
- 还没实测：下次重启后不登录，看开机后一分钟内 `history.log` 有没有新的一行、`ec.log` 有没有 `was 95-100`。

## 没弄清的

- `0x02E5`、`0x02E3` 在 EC 固件里的含义（要 EC 固件或电脑管家的调用记录才能定）。
- GCII 输入电压的单位；GCII 的电流是实时值还是设定值。
- 机器空闲时充电电流能否回到 2 A 以上（用来坐实负载是原因）。
