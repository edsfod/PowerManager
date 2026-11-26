<!--
类别：操作指南（实现状态）。装：换机型时按顺序要做的事。不装：硬件事实（→ reference/）、决策（→ adr/）。
-->

# 新 Windows 机型适配指南（Porting Guide）

当更换到一台新的 Windows 11 笔记本并需要迁移本工具时，按以下步骤执行移植与硬件适配。

---

## 第一步：平台与固件能力基线探测

确定新机型的睡眠、按键与充电基础能力，用于决定需要保留或裁剪哪些模块：

1. 运行 [verify_power_boundaries.py](../../scripts/verify_power_boundaries.py) 的 `check-s3`（`powercfg /a`），确认新机是仅支持 S0 现代待机，还是原生支持传统 S3 睡眠。
2. 运行 `verify_power_boundaries.py test-api-sleep` 与 `heartbeat`，核验 API 请求睡眠是否会退化为休眠、电源键能否单独关屏不冻结后台、合盖后是否会发生意外唤醒。
3. 检查新机 BIOS 或官方轻量工具是否已原生提供固定充电上限开关。若原生已满足且无需软件干预，直接跳过第二步，只复用睡眠调度模块。

---

## 第二步：硬件接口调研与独立验证

仅在需要由本工具接管充电阈值或电池温度读取时执行：

1. 优先检索 Linux 内核对应品牌驱动（`drivers/platform/x86/`）或提取注册表 `HKLM\HARDWARE\ACPI` 中的 ACPI 表反编译，定位充电启停阈值与电池温度的 WMI/EC 接口。
2. 仿照 [verify_wmi_readonly.py](../../scripts/verify_wmi_readonly.py) 编写新机型的**独立只读验证脚本**，先读通传感器与阈值。
3. 实测写入阈值后，对照标准 WMI `BatteryStatus` 的 `ChargeRate` 与 `RemainingCapacity` 物理变化，确认停充确实生效（不轻信接口返回值）。

---

## 第三步：沉淀参考文档与更新边界决策

1. 在 `docs/reference/` 下记录新机型的硬件接口契约与实测电源边界。
2. 原地改写 [ADR-0001](../adr/0001-scope-and-single-model.md) 的适用范围（ADR 描述当前决策，不另写一份新 ADR 去取代它）。

---

## 第四步：实现硬件适配器与配置参数

依据 `Core/Abstractions/IHardwareAdapter.cs` 定义的接口边界，只编写新机型的底层硬件适配器（暴露读写阈值、读电池温度等最小语义接口）及策略配置参数；不修改上层状态机、闹钟、日志解析与通知逻辑。

---

## 第五步：回归验证与部署切换

运行纯函数单元测试与新机型的实机接口验证脚本，确认无误后通过安装脚本重新注册计划任务与快捷方式。
