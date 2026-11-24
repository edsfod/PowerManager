# 未来拓展方向（Roadmap）

本文档仅记录**未排期的远期演进方向与前置演进条件**，不构成当前版本的验收需求（当前生效需求见 [requirements.md](requirements.md)，当前生效设计见 [design.md](design.md)）。

---

## 一、 高频实时遥测与功耗/多点温度曲线

- **方向描述**：在交互界面中以秒级频率展示整机功耗、电池充放电功率与多点 EC 温度传感器实时曲线，用于高负载散热与功耗排查。
- **架构前置条件**：当前 `PowerManager.BatteryCharge` 采用按需 S4U 计划任务（[ADR-0006](adr/0006-csharp-architecture-ipc-and-data-dir.md)），不适合高频冷启动；落地本方向时，仅需将最外层的 S4U 任务外壳替换为常驻 Windows Service 并通过带当前用户 SID ACL 的 Named Pipe 提供查询接口，`PowerManager.Core` 与 `PowerManager.Hardware.MateBook` 无需重写。

---

## 二、 自适应睡眠、温控与电源模式调度

- **方向描述**：基于历史使用时段、负载特征与温度变化率，探索自适应的电源模式切换或散热预警策略（可结合统计模型或轻量神经网络）。
- **边界约束**：不触碰 [ADR-0003](adr/0003-rejection-of-adaptive-charge.md) 已明确否决的自适应充电电流控制，亦不破坏 [requirements.md](requirements.md) 中“手动切换普通/保持运行模式”的显式可控原则（若引入自适应能力，须作为独立可选策略并先经过离线日志回放验证）。

---

## 三、 电池健康度与充放电历史统计

- **方向描述**：基于 `history.log` 与 `BatteryFullChargedCapacity` 的长期记录，在 UI 或 `pmctl` 中提供离线电池满充容量衰减曲线、每日充放电循环统计与异常耗电摘要。
- **架构前置条件**：保持只读离线分析，不增加后台轮询开销（符合 `NFR-01`）。

---

## 四、 新 Windows 机型硬件适配器接入

- **方向描述**：未来更换其他 Windows 11 笔记本时，按 [porting-guide.md](porting-guide.md) 的 5 步硬件移植流程新增独立的 `PowerManager.Hardware.<Model>` 适配器实现，复用 `Core`、`Platform`、`Agent`、`UI` 与 `pmctl`。

---

## 五、 交互式双向 Toast 通知

- **方向描述**：将当前的单向只读通知拓展为支持交互操作按钮（例如保持运行到期时直接在横幅上点击“延长 2 小时”，或低电量/出发提醒时快捷发起冲高）以及多类通知模板与静默时段策略。
- **架构与文档前置条件**：需要为非打包桌面程序注册 COM Activator（`NotificationActivator`）或自定义协议 URI 回调通道，并在 `PowerManager.Agent` 中处理来自通知中心的反向指令；一旦引入上述双向协议与回调契约，应从 [windows-standby-and-toast.md](reference/windows-standby-and-toast.md) 拆出独立的通知接口文档统一归档。

---

## 六、 独立可复用的桌面 UI 壳与设计原子模板

- **方向描述**：将当前 `PowerManager.UI` 中与电源业务无关的通用桌面基础设施——WinForms + WebView2 宿主、DWM 深浅色标题栏同步、单实例 `Mutex` 激活、DPI 自适应窗口缩放动画、JSON-RPC 桥接层与 Fluent Design Tokens——抽离为独立可复用的项目模板或共享库，供其他 Windows 轻量工具直接复用。
- **架构前置条件**：保持当前单页前端与宿主之间的 JSON-RPC 契约（见 [design.md](design.md) §四）稳定，抽离后 PowerManager 仅提供业务后端服务与业务页面，不破坏零 Node 构建链的轻量约束。
