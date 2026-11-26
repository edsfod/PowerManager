<!--
类别：外部事实（本机硬件与 Windows 的客观行为）。装：实测或文档可查的事实与验证方法、尚未查明的项。
不装：本项目怎么用这些事实（→ design.md）、为什么这样用（→ adr/）。
-->

# MateBook 14 2024 电池充电 WMI 接口与 EC 特性参考

- **适用硬件**：MateBook 14 2024（`FLMH-XX`），基于 BIOS 1.17（2025-04-22）ACPI 表 `SSDT`（OEM Table ID: `WmiTable`，OEM Revision: `0x00001000`）验证
- **验证脚本**：[verify_wmi_readonly.py](../../scripts/verify_wmi_readonly.py)

---

## 一、 访问权限、时序与接口编解码规范

### 1. 权限与耗时边界
- **需管理员权限**：`root\wmi` 下的 `OemWMIMethod.OemWMIfun`（所有 EC 读写指令）。
- **无需管理员权限**：同在 `root\wmi` 下的 Windows 标准类 `BatteryStatus`（`PowerOnline`、`Charging`、`ChargeRate`、`DischargeRate`、`RemainingCapacity`）与 `BatteryFullChargedCapacity`。
- **调用耗时**：建立 WMI 连接约 30 ms，每条 EC 指令耗时约 80 ms，全量读取所有温度传感器与阈值耗时约 1.5 秒；不提权读取 `BatteryStatus` 约 46 ms。

### 2. `OemWMIfun` 输入与输出字节格式
- **ACPI 提取来源**：从注册表 `HKLM\HARDWARE\ACPI` 导出 DSDT 与 SSD1…SSDT（共 31 张 `.aml`），使用 `acpica-tools` 执行 `iasl -e DSDT.aml -d WmiTable.aml` 反编译。
- **输入（`u8Input`）**：固定 64 字节数组。前几个字节填入主功能号、子功能号与参数，其余字节补 `0` 至 64 字节。
- **输出（`u8Output`）**：`u8Output[0] == 0` 表示调用成功；多字节 16 位整数按小端序解析为 `u8Output[1] + 256 * u8Output[2]`。具体特例见下表。

---

## 二、 ACPI / WMI 方法与 EC 寄存器映射

WMI 设备 `\_SB.HWMI`（`PNP0C14`）的 `WMAA` 按 `u8Input[0]`（主功能号）与 `u8Input[1]`（子功能号）分派：

| 输入 (`u8Input`) | ASL 方法 | 返回值 (`u8Output`) 解析 | 功能与 EC 地址说明 |
|---|---|---|---|
| `02 02 n` | `GTMP` | `o[0]==0` 时：`o[1]==1` 为负，温度 `(o[2] + o[3]/10.0)` °C | 读温度传感器 `n = 0..15` |
| `02 05 rid` | `GPCI` | `rid=0` 时 `o[1]` 为整机功耗（W） | 读功耗（EC `0x025B`）：`rid=0` 整机、`1` CPU（读数非标准瓦数） |
| `02 0C rid` | `GBAI` | `rid=0x15` 时小端 16 位 `o[1]+256*o[2]`，单位 0.1 K（`val != 0` 时换算为 `val/10 - 273.15` °C；`val == 0` 视为无效读数） | 读电池信息（EC `0x0280`）：`rid=1` 充电电流、`2` 充电电压、`3` 电池状态、`0x0B` SOH、`0x0C` 安全状态、`0x0D` 永久故障、`0x0F` 充电状态、`0x15` NTC 温度 |
| `02 0D rid` | `GCII` | `rid=0` 时小端 16 位 `o[1]+256*o[2]`（mA） | 读充电芯片（EC `0x0263`）：`rid=0` 充电电流 mA、`1` 充电电压 mV、`4` PROCHOT、`5` 充电状态、`6` 输入电流 mA、`7` 输入电压、`8` 最大充电功率 |
| `02 0E rid` | `GADI` | — | 读适配器（EC `0x0263`）：`rid=0` 协议、`1` 最大功率、`2` 线缆信息 |
| `03 10 s e` | `SBTT` | `o[0] == 0` | 写充电阈值（EC `0x0268`）：先写子项 3 = 90，再写子项 1 = 启充 `s`、子项 2 = 停充 `e`（`0 ≤ s ≤ e ≤ 100`；`e=0` 时只写启充） |
| `03 11` | `GBTT` | `o[0..2]` 为 `[状态, 启充, 停充]`；`o[1]==0xFF` 或 `o[2]==0xFF` 视为失败 | 读充电阈值（EC `0x0269` 子项 1、2） |
| `03 12 a` | `SBAC` | `o[0] == 0` | 写 Active Charge Flag（EC `0x02E5`）：`a=1` 写 1，`a=2` 写 0，其余值忽略 |
| `03 13` | `GBAC` | `o[1]` 为标志值 | 读 EC `0x02E6`（读到 0 时改报 2） |
| `03 14` | `GAIT` | — | 读 AC-IN Time（EC `0x02E2`） |
| `03 15 b0 b1 b2 b3` | `SBCM` | `o[0] == 0` | 写充电模式（EC `0x02E3`）：原样写入输入第 2–5 字节 |
| `03 16` | `GBCM` | `o[0..4]` 原始字节 | 读充电模式（EC `0x02E4`） |
| `03 18 …` | `STSP` | — | 写 DPTF 变量 `ODV7/8/9` 并通知 `IETM`（温控，非充电） |
| `04 …` | `SSCV` | — | 调试串“Set full charge voltage”（EC `0x03BF`）；未实测 |

注：`0x10` / `0x11` 与 Linux 内核 `drivers/platform/x86/huawei-wmi.c` 中的 `BATTERY_THRESH_SET (0x1003)` / `BATTERY_THRESH_GET (0x1103)`（小端即 `03 10` / `03 11`）一致。

---

## 三、 华为电脑管家导出符号对照

样本：`PCManager_Setup_14.0.7.310(C233)_x64.exe`（仅检查导出符号与字符串，未反汇编）。

| 管家函数 / 配置 (`WmiUtil.dll` / `SmartChargePlugin.dll`) | 对应 WMI 指令 | 官方逻辑语义 |
|---|---|---|
| `HwSDK::BiosWmi::SetBatteryChargeThreshlod(u8, u8)` | `SBTT (0x10)` | 设定启充与停充百分比 |
| `SetSmartChargeMode(...)` / `GetSmartChargeMode` | `SBCM (0x15)` / `GBCM (0x16)` | 切换“智能充电模式”与“自定义充电模式（50/60/70/80/90/100）” |
| `SetResumeChargingFlag(bool)` / `GetResumeChargingFlag` | `SBAC (0x12)` / `GBAC (0x13)` | 在智能充电控住电量后，点击“恢复充电”以在本次插电周期内（`in one ac duration`）放开限制 |
| `GetAcDurationTime` | `GAIT (0x14)` | 获取插电时长 |

---

## 四、 硬件行为特性与验收准则

1. **`0x10`（SBTT）直接控制充放电与供电切换**：
   - 将阈值设为低于当前电量时，约 3 秒内充电芯片电流降为 0，Windows `Charging` 状态变为 `False`，系统在插电状态下切换为电池放电供电；调回高于当前电量的阈值后立即恢复充电，并在到达停充值时停充。
   - **生效验收准则**：判断充电阈值是否真正生效，**不能仅看 `u8Output[0] == 0`**，必须对照标准 WMI `BatteryStatus` 中 `ChargeRate` 归零及 `RemainingCapacity` 不再上涨等物理充放电行为。
2. **重启后 EC 阈值会复位**：
   - 系统重启后（停在登录界面未进入桌面时），EC 阈值会恢复为 `95–100`。
3. **EC 主动热保护降档（非 Intel DTT 控制）**：
   - 本机适配器最大功率 65 W（20 V），电池请求上限为 12000 mA / 13440 mV（3 串，单串 4.48 V）。
   - 当系统高温或高负载时，EC 会主动将充电芯片电流设定值（`GCII rid 0`，以 `64 mA` 为步进）压低至 `448 mA`（`7 × 64 mA`，约 5.2 W）；系统降温且功耗回落（14–25 W）后，EC 逐档放开至 `960 mA`（`15 × 64 mA`，约 11.6 W）和 `1984 mA`（`31 × 64 mA`，约 23.7 W）。
   - ACPI 中 Intel DTT 的 `\_SB.IETM.CHRG` 引用了 `H_EC`，而本机 EC 设备名为 `HWEC`，因此 Intel DTT 不参与充电电流控制，限流完全由 EC 固件执行。
4. **`0x12`（SBAC）与 `0x15`（SBCM）的边界**：
   - 未触发智能充电限充时，写入 `SBAC(1)` 或 `SBAC(2)` 均返回状态 0，但 `GBAC` 保持 1 且不切断充电。
   - 仅写入 `SBCM (0x15)` 无法阻止电池充过 80%。

---

## 五、 尚未查明的底层硬件字段

- [ ] `0x02E5`、`0x02E3` 在 EC 固件内部的具体状态机逻辑。
- [ ] `GCII rid 7`（输入电压原始值 `0,0,5`）的单位换算，以及 `GCII` 电流读数为芯片设定值还是实时采样值。
- [ ] 系统完全空闲时，EC 放开的充电电流能否超过 2 A。
- [ ] 重启后不登录等待 1 分钟，确认 `95–100` 是 EC/BIOS 复位默认值还是开机驻留服务写入（卸载电脑管家后，本机当前仍运行的华为服务有触控板 `HuaweiThpService`、显示 `HWDisplayService`、音效 `HWVEAudioService`）。
