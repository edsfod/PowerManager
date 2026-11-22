"""PowerManager window (desktop / Start menu `PowerManager.lnk`): power mode and charge boost in one place.

- Mode: normal, or "keep running" for 2/4/8/12/24 hours. Clicking writes ../agent/var/request.json and
  posts the reload message to the agent's hidden window; the agent does the rest.
- Battery: level, threshold band, EC readings, and a temporary boost (../battery-charge/battery_charge.py
  does the work through the elevated scheduled task Battery-ChargeLimit). There is deliberately no
  "stop boost" / "charge to 100% for good" button (user's rule, 2026-11-08).
The window refreshes when it gains focus and after each action; it never polls.
"""
import ctypes
import re
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
u32 = ctypes.windll.user32

# Windows 11 light palette
BG, CARD, LINE = '#F3F3F3', '#FFFFFF', '#E3E3E3'
INK, INK2, INK3 = '#1B1B1B', '#5C5C5C', '#8A8A8A'
ACCENT, ACCENT_HOVER, ACCENT_SOFT = '#0F6CBD', '#115EA3', '#EAF2FB'
GREEN, AMBER, RED = '#0F7B0F', '#9D5D00', '#C42B1C'
CHIP, CHIP_HOVER = '#F5F5F5', '#EBEBEB'
F = 'Microsoft YaHei UI'
FONT, SMALL, TITLE, BIG = (F, 11), (F, 10), (F, 12, 'bold'), ('Segoe UI Variable Display', 36, 'bold')
MODE_ZH = {'efficiency': '最佳能效', 'balanced': '平衡', 'performance': '最佳性能'}


# ------------------------------------------------------------------ data → text

def agent_hwnd():
    return u32.FindWindowW(AGENT_WINDOW_CLASS, None)


def read_mode():
    m = load_json(MODE_FILE, {}) or {}
    until = m.get('run_until_utc')
    if m.get('mode') == 'run' and until and parse_iso(until) > now_utc():
        return 'run', parse_iso(until)
    return 'normal', None


def fmt_left(until):
    s = int((until - now_utc()).total_seconds())
    h, m = s // 3600, s % 3600 // 60
    return (f'{h} 小时 {m} 分' if h else f'{m} 分钟'), until.astimezone().strftime('%H:%M')


def reason_zh(text):
    """battery_charge's English boost reasons, in Chinese."""
    if not text:
        return ''
    pats = [
        (r'Held >= (\d+)% on AC for (\d+) minutes', r'充到 \1% 后插电保持满 \2 分钟'),
        (r'Unplugged for (\d+) minutes', r'没充到就拔电满 \1 分钟'),
        (r'Unplugged on battery', '充到后拔了电'),
        (r'Max boost duration \(([\d.]+)h\) elapsed', r'到了最长 \1 小时'),
    ]
    m = re.match(r'Auto-reverted to .*?: (.*)', text)
    if m:
        r = m.group(1)
        for a, b in pats:
            r = re.sub(a, b, r)
        return f'上次冲高已回落（{r}）'
    if text.startswith('Daily baseline'):
        return ''
    if text.startswith('Boost started'):
        return '冲高中'
    return text


# ------------------------------------------------------------------ widgets

def make_chip(parent, text, command, selected=False, width=None, primary=False, soft=False):
    import tkinter as tk
    w = tk.Label(parent, text=text, font=FONT, cursor='hand2', padx=12, pady=5, width=width or 0, bd=0,
                 highlightthickness=1)
    w.selected = selected
    w.primary = primary
    w.soft = soft

    def paint(hover=False):
        if w.selected and w.soft:
            w.config(bg=ACCENT_SOFT, fg=ACCENT, highlightbackground=ACCENT)
        elif w.selected or w.primary:
            w.config(bg=ACCENT_HOVER if hover else ACCENT, fg='white', highlightbackground=ACCENT)
        else:
            w.config(bg=CHIP_HOVER if hover else CHIP, fg=INK, highlightbackground=LINE)
    w.paint = paint
    w.bind('<Enter>', lambda e: paint(True))
    w.bind('<Leave>', lambda e: paint(False))
    w.bind('<Button-1>', lambda e: command())
    paint()
    return w


def card(parent, title, row):
    import tkinter as tk
    outer = tk.Frame(parent, bg=LINE, padx=1, pady=1)
    outer.grid(row=row, column=0, sticky='nsew', pady=(0, 12))
    inner = tk.Frame(outer, bg=CARD, padx=18, pady=14)
    inner.pack(fill='both', expand=True)
    head = tk.Frame(inner, bg=CARD)
    head.pack(fill='x')
    tk.Label(head, text=title, font=TITLE, bg=CARD, fg=INK).pack(side='left')
    inner.outer = outer
    return inner, head


# ------------------------------------------------------------------ window

def run_gui():
    import tkinter as tk
    from tkinter import messagebox

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('PowerManager')
    except Exception:
        pass

    root = tk.Tk()
    import tkinter.font as tkf
    global FONT, SMALL, TITLE, BIG, TINY, HEAD, FONTS
    FONTS = {name: (tkf.Font(root, family=fam, size=size, weight=weight), size) for name, (fam, size, weight) in {
        'FONT': (F, 11, 'normal'), 'SMALL': (F, 10, 'normal'), 'TITLE': (F, 12, 'bold'), 'TINY': (F, 9, 'normal'),
        'HEAD': (F, 13, 'bold'), 'BIG': ('Segoe UI Variable Display', 36, 'bold')}.items()}
    FONT, SMALL, TITLE, TINY, HEAD, BIG = (FONTS[k][0] for k in ('FONT', 'SMALL', 'TITLE', 'TINY', 'HEAD', 'BIG'))
    root.title('PowerManager')
    root.configure(bg=BG)
    root.resizable(True, True)
    ico = Path(__file__).with_name('icon.ico')
    if ico.exists():
        try:
            root.iconbitmap(default=str(ico))
        except Exception:
            pass
    page = tk.Frame(root, bg=BG, padx=20, pady=18)
    page.pack(fill='both', expand=True)
    page.columnconfigure(0, weight=1, minsize=600)
    ui = {'hours': 12}

    # ---------------- header: agent status
    top = tk.Frame(page, bg=BG)
    top.grid(row=0, column=0, sticky='ew', pady=(0, 12))
    tk.Label(top, text='电源与充电', font=HEAD, bg=BG, fg=INK).pack(side='left')
    agent_box = tk.Frame(top, bg=BG)
    agent_box.pack(side='right')
    agent_dot = tk.Label(agent_box, text='●', font=SMALL, bg=BG)
    agent_dot.pack(side='left')
    agent_lbl = tk.Label(agent_box, font=SMALL, bg=BG, fg=INK2)
    agent_lbl.pack(side='left', padx=(2, 0))
    agent_btn = tk.Label(agent_box, text='启动', font=SMALL, bg=BG, fg=ACCENT, cursor='hand2')
    agent_btn.bind('<Button-1>', lambda e: start_agent())

    # ---------------- mode card
    mode_card, _ = card(page, '模式', 1)
    seg = tk.Frame(mode_card, bg=CARD)
    seg.pack(fill='x', pady=(10, 0))
    btn_normal = make_chip(seg, '普通', lambda: send('normal'), width=10)
    btn_normal.pack(side='left')
    btn_run = make_chip(seg, '保持运行', lambda: send('run'), width=10)
    btn_run.pack(side='left', padx=(6, 0))
    mode_state = tk.Label(seg, font=FONT, bg=CARD, fg=INK2)
    mode_state.pack(side='right')

    hours_row = tk.Frame(mode_card, bg=CARD)
    hours_row.pack(fill='x', pady=(10, 0))
    tk.Label(hours_row, text='保持运行多久', font=SMALL, bg=CARD, fg=INK3).pack(side='left', padx=(0, 8))
    hour_chips = {}
    for h in RUN_HOURS:
        c = make_chip(hours_row, f'{h} 小时', lambda h=h: pick_hours(h), soft=True)
        c.config(padx=8, pady=3, font=SMALL)
        c.pack(side='left', padx=(0, 4))
        hour_chips[h] = c

    rules = tk.Label(mode_card, font=SMALL, bg=CARD, fg=INK2, justify='left', anchor='w')
    rules.pack(fill='x', pady=(12, 0))
    pmode = tk.Label(mode_card, font=SMALL, bg=CARD, fg=INK3, justify='left', anchor='w')
    pmode.pack(fill='x', pady=(4, 0))

    # ---------------- battery card
    bat_card, bat_head = card(page, '电池', 2)
    read_lbl = tk.Label(bat_head, font=SMALL, bg=CARD, fg=INK3)
    read_lbl.pack(side='right')
    row = tk.Frame(bat_card, bg=CARD)
    row.pack(fill='x', pady=(6, 0))
    pct_lbl = tk.Label(row, font=BIG, bg=CARD, fg=INK)
    pct_lbl.pack(side='left')
    right = tk.Frame(row, bg=CARD)
    right.pack(side='left', padx=(14, 0), fill='x', expand=True)
    flow_lbl = tk.Label(right, font=FONT, bg=CARD, fg=INK, anchor='w')
    flow_lbl.pack(fill='x', pady=(8, 0))
    band_lbl = tk.Label(right, font=SMALL, bg=CARD, fg=INK2, anchor='w')
    band_lbl.pack(fill='x')
    bar = tk.Canvas(bat_card, height=1, bg=CARD, highlightthickness=0)
    bar.pack(fill='x', pady=(10, 0))
    metrics = tk.Label(bat_card, font=SMALL, bg=CARD, fg=INK2, anchor='w', justify='left')
    metrics.pack(fill='x', pady=(8, 0))
    note = tk.Label(bat_card, font=SMALL, bg=CARD, fg=AMBER, anchor='w', justify='left')
    note.pack(fill='x')

    tk.Frame(bat_card, bg=LINE, height=1).pack(fill='x', pady=12)

    # boost
    boost_box = tk.Frame(bat_card, bg=CARD)
    boost_box.pack(fill='x')
    boost_on = tk.Frame(boost_box, bg=ACCENT_SOFT, padx=12, pady=10)
    boost_on_lbl = tk.Label(boost_on, font=FONT, bg=ACCENT_SOFT, fg=INK, justify='left', anchor='w')
    boost_on_lbl.pack(fill='x')
    boost_off = tk.Frame(boost_box, bg=CARD)
    line1 = tk.Frame(boost_off, bg=CARD)
    line1.pack(fill='x')
    tk.Label(line1, text='临时充到', font=FONT, bg=CARD, fg=INK).pack(side='left')
    targets = tk.Frame(line1, bg=CARD)
    targets.pack(side='left', padx=(10, 0))
    prefs = (load_json(BATTERY_STATE, {}) or {}).get('ui_prefs', {})
    ui['target'] = int(prefs.get('target_stop', 95))
    target_chips = {}
    for t in (85, 90, 95, 100):
        c = make_chip(targets, f'{t}%', lambda t=t: pick_target(t), soft=True)
        c.config(padx=10, pady=3)
        c.pack(side='left', padx=(0, 4))
        target_chips[t] = c
    opts = tk.Frame(boost_off, bg=CARD)
    opts.pack(fill='x', pady=(10, 0))
    hold_var = tk.StringVar(value=str(prefs.get('full_hold_minutes', 60)))
    maxh_var = tk.StringVar(value=str(prefs.get('max_total_hours', 4.0)))
    tk.Label(opts, text='充到后插着电保持', font=SMALL, bg=CARD, fg=INK2).pack(side='left')
    tk.Spinbox(opts, from_=10, to=360, increment=15, textvariable=hold_var, width=4, font=SMALL, relief='flat',
               bg=CHIP, buttonbackground=CHIP, highlightthickness=1, highlightbackground=LINE).pack(side='left', padx=4)
    tk.Label(opts, text='分钟，最长', font=SMALL, bg=CARD, fg=INK2).pack(side='left')
    tk.Spinbox(opts, from_=1.0, to=12.0, increment=0.5, textvariable=maxh_var, width=4, font=SMALL, relief='flat',
               bg=CHIP, buttonbackground=CHIP, highlightthickness=1, highlightbackground=LINE).pack(side='left', padx=4)
    tk.Label(opts, text='小时；拔电出门也会回到 75%–80%', font=SMALL, bg=CARD, fg=INK2).pack(side='left')
    boost_btn = make_chip(line1, '开始冲高', lambda: boost(), primary=True)
    boost_btn.pack(side='right')

    # ---------------- actions
    def notify_agent():
        hwnd = agent_hwnd()
        if hwnd:
            u32.PostMessageW(hwnd, u32.RegisterWindowMessageW(AGENT_RELOAD_MSG), 0, 0)
        return hwnd

    def send(mode):
        if not agent_hwnd():
            messagebox.showwarning('常驻程序没在运行', '点右上角“启动”，或以管理员身份运行一次 install.ps1。', parent=root)
            return
        save_json(REQUEST_FILE, {'mode': mode, 'hours': ui['hours'], 'requested_at_utc': iso(now_utc())})
        notify_agent()
        root.after(600, refresh)

    def pick_hours(h):
        ui['hours'] = h
        if read_mode()[0] == 'run':
            send('run')       # already running: restart with the new duration
        else:
            refresh()

    def pick_target(t):
        ui['target'] = t
        refresh()

    def start_agent():
        subprocess.run(['schtasks', '/Run', '/TN', AGENT_TASK], capture_output=True, creationflags=NO_WINDOW)
        root.after(2000, refresh)

    def boost():
        try:
            h, m = int(hold_var.get()), float(maxh_var.get())
            if not (5 <= h <= 720 and 0.5 <= m <= 24):
                raise ValueError
        except ValueError:
            messagebox.showerror('参数有误', '保持 5–720 分钟，最长 0.5–24 小时。', parent=root)
            return
        boost_btn.config(text='正在写入 EC…')
        root.update_idletasks()
        ok = bc.request_boost(ui['target'], h, m)
        notify_agent()
        boost_btn.config(text='开始冲高')
        refresh()
        if not ok:
            messagebox.showwarning('计划任务没有响应', '计划任务 Battery-ChargeLimit 没有响应：以管理员身份运行一次 '
                                   'install.ps1，或看 battery-charge\\var\\error.log。', parent=root)

    # ---------------- refresh
    def draw_bar(pct, band):
        bar.update_idletasks()
        f = TINY
        th = f.metrics('linespace')
        w, h = max(bar.winfo_width(), 100), max(10, th // 2)
        bar.config(height=h + 4 + th)
        bar.delete('all')
        bar.create_rectangle(0, 0, w, h, fill='#EDEDED', outline='')
        lo, hi = band
        bar.create_rectangle(w * lo / 100, 0, w * hi / 100, h, fill='#D6E6F7', outline='')
        if isinstance(pct, int):
            color = GREEN if pct >= 50 else (AMBER if pct >= 20 else RED)
            bar.create_rectangle(0, 2, w * pct / 100, h - 2, fill=color, outline='')
        for x in (lo, hi):
            bar.create_line(w * x / 100, 0, w * x / 100, h, fill=ACCENT)
        y = h + 3
        bar.create_text(0, y, text='0', anchor='nw', font=f, fill=INK3)
        bar.create_text(w / 2, y, text='50', anchor='n', font=f, fill=INK3)
        bar.create_text(w * (lo + hi) / 200, y, text=f'{lo}–{hi}', anchor='n', font=f, fill=ACCENT)
        bar.create_text(w, y, text='100', anchor='ne', font=f, fill=INK3)
        ui['bar'] = (pct, band)

    def refresh(_=None):
        alive = bool(agent_hwnd())
        agent_dot.config(fg=GREEN if alive else RED)
        agent_lbl.config(text='常驻运行中' if alive else '常驻没在运行')
        agent_btn.pack_forget() if alive else agent_btn.pack(side='left', padx=(6, 0))

        mode, until = read_mode()
        btn_normal.selected, btn_run.selected = mode == 'normal', mode == 'run'
        btn_normal.paint(), btn_run.paint()
        if mode == 'run':
            left, at = fmt_left(until)
            mode_state.config(text=f'还剩 {left}，{at} 回到普通', fg=ACCENT)
            rules.config(text='插电：不睡，合盖继续跑，15 分钟关屏\n用电池：30 分钟没人操作才睡，电池到 40 °C 提前睡')
        else:
            mode_state.config(text='合盖 1 分钟后睡', fg=INK2)
            rules.config(text='插电：15 分钟关屏，1 小时睡\n用电池：5 分钟关屏，15 分钟睡')
        for h, c in hour_chips.items():
            c.selected = h == ui['hours']
            c.paint()
        ac, pct = power.os_power()
        pmode.config(text=f"电源模式：插电 {MODE_ZH.get(power.get_power_mode(True), '?')}，用电池 "
                          f"{MODE_ZH.get(power.get_power_mode(False), '?')}（电量 50% 以下自动换最佳能效）")

        st = load_json(BATTERY_STATE, {}) or {}
        ec, win, b = st.get('ec') or {}, st.get('win') or {}, st.get('boost') or {}
        target = st.get('target') or [bc.DAILY_START, bc.DAILY_STOP]
        pct_lbl.config(text=f'{pct}%' if pct is not None else '?')
        if ac:
            if win.get('charge_mw'):
                flow = f"接通电源 · 充电 {win['charge_mw'] / 1000:.1f} W"
            elif isinstance(pct, int) and pct >= target[1] - 1:
                flow = f'接通电源 · 已到 {target[1]}%，不再充'
            else:
                flow = '接通电源 · 不充电'
        else:
            flow = '使用电池' + (f" · 放电 {win['discharge_mw'] / 1000:.1f} W" if win.get('discharge_mw') else '')
        flow_lbl.config(text=flow)
        if ec.get('hw_ok'):
            hw = [ec['hw_start'], ec['hw_stop']]
            band_lbl.config(text=f'充电区间 {hw[0]}%–{hw[1]}%' + ('' if hw == list(target) else f'（应为 {target[0]}%–{target[1]}%，下次运行改回）'),
                            fg=INK2 if hw == list(target) else AMBER)
        else:
            band_lbl.config(text='充电区间：还没读到', fg=AMBER)
        draw_bar(pct, target)
        parts = []
        if ec.get('battery_c') is not None:
            parts.append(f"电池 {ec['battery_c']:.1f} °C")
        if ec.get('system_w') is not None:
            parts.append(f"整机 {ec['system_w']} W")
        if ec.get('charge_ma') is not None:
            parts.append(f"充电电流 {ec['charge_ma']} mA")
        if ec.get('hottest_c') is not None:
            parts.append(f"最热处 {ec['hottest_c']:.0f} °C")
        metrics.config(text='   ·   '.join(parts))
        slow = (ac and win.get('charging') and ec.get('charge_ma') is not None and ec['charge_ma'] < bc.LOW_CHARGE_MA
                and isinstance(pct, int) and pct < target[1] - 1)
        last = reason_zh(b.get('last_reason')) if not b.get('active') else ''
        note.config(text='充得慢：机器热或功耗高时 EC 会压低充电电流，凉下来自动放开' if slow else last,
                    fg=AMBER if slow else INK3)
        if st.get('updated_at_utc'):
            read_lbl.config(text=f"读数 {parse_iso(st['updated_at_utc']).astimezone():%m-%d %H:%M}")

        if b.get('active'):
            boost_off.pack_forget()
            boost_on.pack(fill='x')
            deadline = parse_iso(b['deadline_utc']).astimezone().strftime('%H:%M')
            if b.get('full_since_utc'):
                held = int((now_utc() - parse_iso(b['full_since_utc'])).total_seconds() // 60)
                prog = f"已充到，插着电保持 {held}/{b.get('full_hold_minutes', 60)} 分钟"
            else:
                prog = f"正在充，到了插着电保持 {b.get('full_hold_minutes', 60)} 分钟"
            boost_on_lbl.config(text=f"冲高到 {b.get('target_stop')}%\n{prog}；最迟 {deadline} 回到 75%–80%，拔电也会回")
        else:
            boost_on.pack_forget()
            boost_off.pack(fill='x')
            for t, c in target_chips.items():
                c.selected = t == ui['target']
                c.paint()

    root.bind('<FocusIn>', lambda e: refresh() if e.widget is root else None)
    def zoom(e=None):
        """Bigger window, bigger text: scale every font with the window (1x to 2x)."""
        if 'base' not in ui:
            return
        bw, bh = ui['base']
        z = max(1.0, min(2.0, root.winfo_width() / bw, root.winfo_height() / bh))
        if abs(z - ui.get('zoom', 1.0)) < 0.04:
            return
        ui['zoom'] = z
        for f, size in FONTS.values():
            f.configure(size=round(size * z))
        if 'bar' in ui:
            draw_bar(*ui['bar'])

    root.bind('<Configure>', lambda e: zoom() if e.widget is root else None)
    bar.bind('<Configure>', lambda e: draw_bar(*ui['bar']) if 'bar' in ui else None)
    refresh()
    root.update_idletasks()
    ui['base'] = (root.winfo_reqwidth(), root.winfo_reqheight())
    root.minsize(*ui['base'])
    root.mainloop()


if __name__ == '__main__':
    run_gui()
