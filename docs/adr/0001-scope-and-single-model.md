<!--
类别：决策状态。决策被推翻时原地改写本文，被推翻的方案及原因写进"备选方案"。
-->

# ADR-0001: 只支持 Windows 11 与 MateBook 14 2024，上层保留可迁移边界

## 背景

项目服务于一台华为 MateBook 14 2024（`FLMH-XX`，Windows 11）。需要确定：是否支持其他操作系统；是否为其他品牌或型号预先编写硬件适配。

驱动力：

- 充电阈值与温度读取依赖厂商 WMI / EC 接口，各厂商完全不同；没有真机就无法验证。
- 部分调度策略来自本机实测特性（没有 S3 睡眠、轻触电源键被固件过滤，见 [windows-standby-and-toast.md](../reference/windows-standby-and-toast.md)），换一台机器未必成立。
- 模式切换、消息监听、电源模式与通知都基于标准 Windows API，与机型无关。

## 决策

1. **操作系统**：只支持 Windows 11。
2. **硬件**：只实现 MateBook 14 2024 一套 EC / WMI 适配，不预先编写多品牌兼容或降级逻辑。
3. **隔离**：硬件读写收拢在 `IChargeHardwareAdapter` 接口之后（实现位于 `PowerManager.Hardware.MateBook`，只有 `PowerManager.BatteryCharge` 引用它），上层调度逻辑保持可在其他 Windows 机器上复用。

## 备选方案

1. **支持 Linux / macOS / 鸿蒙**：macOS 由系统闭环管理电源与睡眠；Linux 可直接用 `sysfs`（如内核自带的 `huawei-wmi`）与 `systemd-logind` / `tlp` 配置完成，无需常驻程序；鸿蒙 PC 不向第三方开放 EC 写入与睡眠控制。收益极低。
2. **预先编写多品牌适配框架**：没有目标真机与固件样本，写出的适配代码无法实测，只会成为死代码。

## 影响

- 不引入多机型抽象，代码聚焦当前设备的稳定性。
- 换机型时只需重写硬件适配器，见 [porting-guide.md](../guides/porting-guide.md)；改动硬件与系统接口前须先实机验证（[ARCHITECTURE.md](../../ARCHITECTURE.md) 横切关注点）。
