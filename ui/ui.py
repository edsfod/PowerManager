"""PowerManager window (desktop / Start menu `PowerManager.lnk`): power mode and charge boost in one place.

- Power mode: normal, or "keep running" for 2/4/8/12/24 hours. The UI writes ../agent/var/request.json
  and posts the reload message to the agent's hidden window; the agent does the rest.
- Charge: status and a temporary boost (../battery-charge/battery_charge.py does the work through the
  elevated scheduled task Battery-ChargeLimit).
"""
import ctypes
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'battery-charge'))
from common import power                                                     # noqa: E402
from common.util import (AGENT_RELOAD_MSG, AGENT_TASK, AGENT_VAR, AGENT_WINDOW_CLASS, BATTERY_STATE,  # noqa: E402
                         NO_WINDOW, iso, load_json, now_utc, parse_iso, save_json)
import battery_charge as bc                                                  # noqa: E402

MODE_FILE = AGENT_VAR / 'mode.json'
REQUEST_FILE = AGENT_VAR / 'request.json'
RUN_HOURS = (2, 4, 8, 12, 24)
FONT = ('Microsoft YaHei UI', 10)
SMALL = ('Microsoft YaHei UI', 9)
TITLE = ('Microsoft YaHei UI', 12, 'bold')
GREY = '#666666'
MODE_LABEL = {'efficiency': 'Best Power Efficiency', 'balanced': 'Balanced', 'performance': 'Best Performance'}
u32 = ctypes.windll.user32


def agent_hwnd():
    return u32.FindWindowW(AGENT_WINDOW_CLASS, None)


def mode_text():
    m = load_json(MODE_FILE, {}) or {}
    until = m.get('run_until_utc')
    lines = []
    if m.get('mode') == 'run' and until and parse_iso(until) > now_utc():
        left = parse_iso(until) - now_utc()
        h, mnt = int(left.total_seconds() // 3600), int(left.total_seconds() % 3600 // 60)
        lines.append(f"当前：保持运行，到 {parse_iso(until).astimezone():%m-%d %H:%M}（还剩 {h} 小时 {mnt} 分）")
        lines.append('插电不睡、合盖继续跑；用电池 30 分钟没人操作才睡，电池 40 °C 提前睡')
    else:
        lines.append('当前：普通')
        lines.append('合盖 1 分钟睡；闲置：插电 15 分钟关屏、1 小时睡，用电池 5 分钟关屏、15 分钟睡')
    ac, pct = power.os_power()
    lines.append(f"电源模式：插电 {MODE_LABEL.get(power.get_power_mode(True), '?')}，"
                 f"用电池 {MODE_LABEL.get(power.get_power_mode(False), '?')}（电量 {pct}%，50% 以下省电）")
    if not agent_hwnd():
        lines.append('⚠ 常驻程序没在运行，上面的模式不会生效（点“启动常驻”）')
    return '\n'.join(lines)


def run_gui():
    import tkinter as tk
    from tkinter import messagebox, ttk

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('PowerManager')
    except Exception:
        pass

    root = tk.Tk()
    root.title('PowerManager')
    root.resizable(False, False)
    ico = Path(__file__).with_name('icon.ico')
    if ico.exists():
        try:
            root.iconbitmap(default=str(ico))
        except Exception:
            pass
    style = ttk.Style()
    style.configure('TButton', font=FONT)
    style.configure('TRadiobutton', font=FONT)
    pad = ttk.Frame(root, padding=18)
    pad.grid(row=0, column=0, sticky='nsew')

    # ---------------- power mode
    ttk.Label(pad, text='电源模式', font=TITLE).grid(row=0, column=0, columnspan=4, sticky='w')
    mode_var = tk.StringVar(value=mode_text())
    ttk.Label(pad, textvariable=mode_var, justify='left', font=FONT).grid(row=1, column=0, columnspan=4, sticky='w', pady=(4, 10))

    hours_var = tk.IntVar(value=12)
    hours_row = ttk.Frame(pad)
    hours_row.grid(row=2, column=0, columnspan=4, sticky='w')
    ttk.Label(hours_row, text='保持运行多久：', font=FONT).pack(side='left')
    for h in RUN_HOURS:
        ttk.Radiobutton(hours_row, text=f'{h} 小时', value=h, variable=hours_var).pack(side='left', padx=4)

    def send(mode):
        hwnd = agent_hwnd()
        if not hwnd:
            messagebox.showwarning('常驻程序没在运行', '先点“启动常驻”，或以管理员身份运行一次 install.ps1。', parent=root)
            return
        save_json(REQUEST_FILE, {'mode': mode, 'hours': hours_var.get(), 'requested_at_utc': iso(now_utc())})
        u32.PostMessageW(hwnd, u32.RegisterWindowMessageW(AGENT_RELOAD_MSG), 0, 0)
        root.after(800, refresh)

    def start_agent():
        subprocess.run(['schtasks', '/Run', '/TN', AGENT_TASK], capture_output=True, creationflags=NO_WINDOW)
        root.after(2000, refresh)

    btns = ttk.Frame(pad)
    btns.grid(row=3, column=0, columnspan=4, sticky='ew', pady=(10, 0))
    ttk.Button(btns, text='保持运行（跑实验、通勤）', command=lambda: send('run')).pack(side='left', expand=True, fill='x', padx=(0, 4), ipady=3)
    ttk.Button(btns, text='回到普通', command=lambda: send('normal')).pack(side='left', expand=True, fill='x', padx=4, ipady=3)
    ttk.Button(btns, text='启动常驻', command=start_agent).pack(side='left', padx=(4, 0), ipady=3)

    ttk.Separator(pad, orient='horizontal').grid(row=4, column=0, columnspan=4, sticky='ew', pady=16)

    # ---------------- charge
    ttk.Label(pad, text='充电（日常 75%–80%）', font=TITLE).grid(row=5, column=0, columnspan=4, sticky='w')
    charge_var = tk.StringVar(value=bc.format_status_text(load_json(BATTERY_STATE, {})))
    ttk.Label(pad, textvariable=charge_var, justify='left', font=FONT).grid(row=6, column=0, columnspan=4, sticky='w', pady=(4, 10))

    prefs = (load_json(BATTERY_STATE, {}) or {}).get('ui_prefs', {})
    target_var = tk.StringVar(value=str(prefs.get('target_stop', 95)))
    hold_var = tk.StringVar(value=str(prefs.get('full_hold_minutes', 60)))
    maxh_var = tk.StringVar(value=str(prefs.get('max_total_hours', 4.0)))

    ttk.Label(pad, text='临时冲高到 (%)：', font=FONT).grid(row=7, column=0, sticky='w', pady=3)
    ttk.Spinbox(pad, from_=85, to=100, increment=5, textvariable=target_var, width=6, font=FONT).grid(row=7, column=1, sticky='w', padx=6)
    quick = ttk.Frame(pad)
    quick.grid(row=7, column=2, columnspan=2, sticky='w')
    for v in (90, 95, 100):
        ttk.Button(quick, text=f'{v}%', width=5, command=lambda v=v: target_var.set(str(v))).pack(side='left', padx=2)
    ttk.Label(pad, text='到了插着电保持 (分钟)：', font=FONT).grid(row=8, column=0, sticky='w', pady=3)
    ttk.Spinbox(pad, from_=10, to=360, increment=15, textvariable=hold_var, width=6, font=FONT).grid(row=8, column=1, sticky='w', padx=6)
    ttk.Label(pad, text='然后回到 75%–80%', foreground=GREY, font=SMALL).grid(row=8, column=2, columnspan=2, sticky='w')
    ttk.Label(pad, text='最长 (小时)：', font=FONT).grid(row=9, column=0, sticky='w', pady=3)
    ttk.Spinbox(pad, from_=1.0, to=12.0, increment=0.5, textvariable=maxh_var, width=6, font=FONT).grid(row=9, column=1, sticky='w', padx=6)
    ttk.Label(pad, text='拔电出门也会回落', foreground=GREY, font=SMALL).grid(row=9, column=2, columnspan=2, sticky='w')

    def boost():
        try:
            t, h, m = int(target_var.get()), int(hold_var.get()), float(maxh_var.get())
            if not (85 <= t <= 100 and 5 <= h <= 720 and 0.5 <= m <= 24):
                raise ValueError
        except ValueError:
            messagebox.showerror('参数有误', '上限 85–100，保持 5–720 分钟，最长 0.5–24 小时。', parent=root)
            return
        boost_btn.config(state='disabled', text='正在写入 EC…')
        root.update_idletasks()
        ok = bc.request_boost(t, h, m)
        hwnd = agent_hwnd()
        if hwnd:   # let the agent arm the boost timers
            u32.PostMessageW(hwnd, u32.RegisterWindowMessageW(AGENT_RELOAD_MSG), 0, 0)
        boost_btn.config(state='normal', text='开始临时冲高')
        refresh()
        if not ok:
            messagebox.showwarning('计划任务没有响应', '计划任务 Battery-ChargeLimit 没有响应：以管理员身份运行一次 '
                                   'install.ps1，或看 battery-charge\\var\\error.log。', parent=root)

    boost_btn = ttk.Button(pad, text='开始临时冲高', command=boost)
    boost_btn.grid(row=10, column=0, columnspan=4, sticky='ew', pady=(12, 0), ipady=3)

    def refresh():
        mode_var.set(mode_text())
        charge_var.set(bc.format_status_text(load_json(BATTERY_STATE, {})))

    root.mainloop()


if __name__ == '__main__':
    run_gui()
