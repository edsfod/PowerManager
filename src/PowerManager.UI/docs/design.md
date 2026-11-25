# 交互界面设计规格（UI Design）

本文档定义 `PowerManager.UI` 的**具体页面架构、窗口与交互机制及需求追踪矩阵（How）**；外部可验收需求见 [requirements.md](requirements.md)，通用 UI 工程规范见 [conventions.md](conventions.md)，跨进程通信契约（10 项 JSON-RPC、Named Pipe、`<DataDir>/requests/`、`prefs.json` 与单实例 `Mutex`）归口于 [docs/design.md](../../../docs/design.md) §四。

---

## 一、 窗口行为与尺寸策略

遵循 [conventions.md](conventions.md) §一–§二（谨慎硬编码与单向尺寸所有权）：

- **宽度**：收起规则面板时采用紧凑逻辑宽度（`720px`），手动展开规则面板时平滑动画至展开逻辑宽度（`960px`）；窗口宽度 `>= 880px`（含最大化）时自动进入展开布局。
- **高度与窗口态识别**：
  - 宿主 `MainForm` 监听 `SizeChanged` 并通过 `window_state` 事件实时通知前端是否最大化（在 `<html>` 上维护 `data-window-state="maximized"` / `"normal"`）。
  - **自由窗口态（`normal`）**：根字号固定 `13px`（不依赖 `vh`），`html` 设置 `scrollbar-gutter: stable;` 防瞬态滚动条左右抖动；前端通过 `ResizeObserver` 监听 `<main>` 真实内容高度并调用 `set_content_height` RPC，由 `WindowWidthAnimator.ApplyContentHeight` 基于垂直中心锚点 `_anchorCenterY` 对称贴合窗口客户区高度（上限工作区 `94%`）；从最大化还原时自动重新贴合。
  - **最大化态（`maximized`）**：窗口尺寸由操作系统锁定（无 `vh` 反馈环），前端切换为视口主导：根字号按视口高度平滑缩放（`clamp(10.6px, 1.56vh, 15.5px)`），放宽 `main` 宽度（`min(76rem, 90vw)`）并在 `100vh` 内垂直居中；极端矮屏（`max-height: 680px`）由媒体查询微调间距兜底。

---

## 二、 信息架构与响应式布局

页面自上而下由顶栏（标题 + 常驻状态胶囊）、**模式卡片（`.mode-card`）**、**电池与冲高卡片（`.battery-card`）**构成：

### 1. 模式卡片（`.mode-card`）
- **收起态**：`.tiles` 横向双列（`普通` / `保持运行`）；`#runbar` 与 `#forced-sleep-bar` 位于其下方通栏。
- **展开态（含宽屏自动展开）**：
  - `.mode-layout` 为左右两栏（`minmax(14rem, 17.5rem) 1fr`）：左栏仅放 `.tiles`（纵向双行），右栏仅放 `.rules-panel`（规则列表 + 快捷键与数据目录页脚），左右高度自然齐平；
  - `#runbar` 与 `#forced-sleep-bar` 独立于 `.mode-layout` 之外，始终位于模式卡片底部通栏。

### 2. 电池与冲高卡片（`.battery-card`）
- **自然流式排布**：不使用 `flex: 1; justify-content: space-between`，消除卡片内 `<hr>` 上下的人为留白。
- **6 项核心指标对称网格（`.stats`）**：`电池温度`、`整机功耗`、`充电电流`、`最热传感器`、`插电`、`用电池` 六项平级排布：
  - **紧凑态（`< 880px`）**：**2 列 × 3 行**；
  - **宽屏 / 展开态（`>= 880px`）**：自动重组为 **3 列 × 2 行**。
- **临时冲高区**：
  - 未冲高时展示 3 组“滑条 + 数值读数 + 快捷档位”与右下角主按钮“开始冲高”；宽屏（`>= 880px`）下每组滑条与快捷档位同行排布，所有滑条受 `max-width: 22rem` 有界约束；
  - 冲高中切换为紧凑进度条卡片。

---

## 三、 保持运行“统一草稿 + 显式确认”状态机

保持运行时长范围 `1–24` 小时（步长 `1` 小时，快捷档位 `2 / 4 / 8 / 12 / 24 小时` 与 `直到手动改回`（`hours = 0`），默认 `12` 小时）：

| 后端真实状态 | 用户交互 | 前端视觉反馈 | 未确认就关闭 / 点取消 | 点击“确认”后 |
|---|---|---|---|---|
| **普通模式** | 点击“保持运行”卡片 | 撤下“普通”高亮，仅“保持运行”显示虚线主题边框 + `待确认` 徽标（中性底色）；底部 `#runbar` 以中性槽位展示滑条、快捷档位（含“直到手动改回”）、预计结束时刻、“取消”与“确认” | 放弃草稿，**维持普通模式**（零 IPC），窗口对称缩回原位 | 发送 `set_mode('run', draftHours)`，切为实线蓝框已生效态并开始倒计时（或显示“直到手动改回”） |
| **保持运行（已生效）** | 拖动滑条或点其他档位 | `#runbar` 保留当前真实倒计时并标注草稿变更，显示“取消”与“确认” | 放弃草稿并复位滑条，**维持原倒计时继续运行**（绝不退回普通） | 发送 `set_mode('run', draftHours)`，按新时长重设到期时间 |
| **保持运行（已生效）** | 点击“普通”卡片 | 立即发送 `set_mode('normal')` 切回普通模式并收起 `#runbar`，窗口对称缩回原位 | — | — |

---

## 四、 主题、离线预览与验收门禁

- **Design Tokens 与主题同步**：全部语义 Token 定义在 [styles.css](../web/styles.css) `:root` 与深色主题块中；宿主监听 Windows 浅/深色切换并同步 DWM 标题栏与 WebView2 背景色。
- **零 Node 构建与离线 Mock**：前端由 `index.html`、`styles.css`、`mock.js`、`app.js` 组成，编译时由 `EmbeddedWebAssets.cs` 内嵌；无宿主时自动回落至 `mock.js` 离线桥接与状态工具栏。
- **验收门禁**：
  1. `python scripts/verify_ui_layout.py`（Edge 无头 Blink 引擎校验 8 组自由/最大化视口高度与零溢出）；
  2. 真实渲染截图目视检查（紧凑普通态、紧凑草稿态、展开草稿态、`1440×912` 最大化态）；
  3. `PowerManager.UI.exe --dry-run --smoke-test` 退出码 `0`。

---

## 五、 UI 需求追踪矩阵（Traceability Matrix）

| 需求编号 | 设计实现机制 | 关键源码 | 验证方式 |
|---|---|---|---|
| `UI-01` | 按管道名隔离的单实例 `Mutex` + `DwmSetWindowAttribute` 与 `UserPreferenceChanged` 热切换 | `Program.cs`、`MainForm.cs`、`EmbeddedWebAssets.cs` | 多开激活核验 + 系统主题切换 |
| `UI-02` | `ResizeObserver` → `set_content_height` + `_anchorCenterY` 对称定位 + `scrollbar-gutter: stable` | `app.js`、`WindowWidthAnimator.cs`、`styles.css` | `verify_ui_layout.py` + 实机切换模式核验 |
| `UI-03` | `MainForm.SizeChanged` → `window_state` 事件 → `html[data-window-state="maximized"]` 视口自适应 + `max-width` 有界滑条 | `MainForm.cs`、`app.js`、`styles.css` | `verify_ui_layout.py` + `1440×912` 截图目视 |
| `UI-04` | `runDraft` 前端草稿状态机 + `1–24`h 滑条与 `0`（直到手动改回）全链路支持 | `app.js`、`PowerModeCalculator.cs`、`AgentController.cs`、`UiStateFormatter.cs` | 单元测试 + 前端交互核验 |
| `UI-05` | `.tile.on` / `.tile.draft` / `.runbar.draft` 正交样式规则与单一主焦点逻辑 | `app.js`、`styles.css` | 草稿态截图目视检查 |
| `UI-06` | `#runbar` 独立于 `.mode-layout` 之外底部通栏 + 左栏仅双 tile、右栏规则面板 | `index.html`、`styles.css` | 展开态截图目视检查 |
| `UI-07` | 6 项平级 `.stat` 卡片，紧凑 `2×3` / 宽屏 `3×2` 对称重组 + `refresh_ec` / `boost` RPC | `index.html`、`styles.css`、`WebRpcBridge.cs` | 紧凑/宽屏截图目视 + `--smoke-test` |
| `UI-08` | `mock.js` 离线回落桥接 + `keydown`（`Esc`/`F5`）与 `open_data_dir` RPC | `mock.js`、`app.js`、`WebRpcBridge.cs` | 浏览器直接打开 `web/index.html` 核验 |
