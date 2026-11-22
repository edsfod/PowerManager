# PowerManager

在这里做事要知道的几条，项目地图在 `README.md`。

- **用户定的**：不留永久充到 100% 的入口，也不设手动恢复按钮（2026-11-08）；EC 发热时自己压低充电电流是保护，不去改（2026-11-11）；电脑管家可以逆向，但不反汇编。
- **时间**：工具日志里用 UTC；文档写本机时间（UTC+8）要注明。
- **提交**：git，`var/` 与 `__pycache__/` 不进库。作者 `local <local@localhost>`，只提交自己改的文件。
- **改 EC 的东西**：看电池的实际行为（`battery-charge/var/history.log` 的 `chg_mW`、`cap_mWh`），不看指令返回值。
