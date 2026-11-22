"""PowerManager window (desktop / Start menu `PowerManager.lnk`): power mode and charge boost in one place.

The window is web/index.html in WebView2 (pywebview, EdgeChromium). This file is its backend (`Api`,
called from the page as window.pywebview.api.*):
- mode: normal, or "keep running" for 2/4/8/12/24 hours. Writes ../agent/var/request.json and posts the
  reload message to the agent's hidden window; the agent does the rest.
- battery: level, threshold band, EC readings, temporary boost (../battery-charge/battery_charge.py does
  the work through the elevated scheduled task Battery-ChargeLimit). Deliberately no "stop boost" or
  "charge to 100% for good" (user's rule, 2026-11-08).
The page refreshes when the window gains focus and after each action; nothing polls.
    pythonw ui.py            open the window
    python ui.py --debug     with WebView2 developer tools
"""
import ctypes
import ctypes.wintypes as wt
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'battery-charge'))
from common import power                                                     # noqa: E402
from common.util import (AGENT_RELOAD_MSG, AGENT_TASK, AGENT_VAR, AGENT_WINDOW_CLASS, BATTERY_STATE,  # noqa: E402
                         NO_WINDOW, iso, load_json, now_utc, parse_iso, save_json)
import battery_charge as bc                                                  # noqa: E402

HERE = Path(__file__).resolve().parent
MODE_FILE = AGENT_VAR / 'mode.json'
REQUEST_FILE = AGENT_VAR / 'request.json'
PREFS_FILE = HERE / 'var' / 'prefs.json'
RUN_HOURS = (2, 4, 8, 12, 24)
MODE_ZH = {'efficiency': '最佳能效', 'balanced': '平衡', 'performance': '最佳性能'}
u32 = ctypes.windll.user32


def agent_hwnd():
    return u32.FindWindowW(AGENT_WINDOW_CLASS, None)


def notify_agent():
    hwnd = agent_hwnd()
    if hwnd:
        u32.PostMessageW(hwnd, u32.RegisterWindowMessageW(AGENT_RELOAD_MSG), 0, 0)
    return bool(hwnd)


def reason_zh(text):
    """battery_charge's English boost reasons, in Chinese ('' for the daily baseline)."""
    m = re.match(r'Auto-reverted to .*?: (.*)', text or '')
    if not m:
        return ''
    r = m.group(1)
    for a, b in [(r'Held >= (\d+)% on AC for (\d+) minutes', r'充到 \1% 后插电保持满 \2 分钟'),
                 (r'Unplugged for (\d+) minutes', r'没充到就拔电满 \1 分钟'),
                 (r'Unplugged on battery', '充到后拔了电'),
                 (r'Max boost duration \(([\d.]+)h\) elapsed', r'到了最长 \1 小时')]:
        r = re.sub(a, b, r)
    return f'上次冲高已回落：{r}'


def local_hm(t):
    return t.astimezone().strftime('%H:%M')


class Api:
    def get_state(self):
        prefs = load_json(PREFS_FILE, {}) or {}
        m = load_json(MODE_FILE, {}) or {}
        until = m.get('run_until_utc')
        mode = {'mode': 'normal', 'hours': prefs.get('hours', 12)}
        if m.get('mode') == 'run' and until and parse_iso(until) > now_utc():
            u = parse_iso(until)
            s = int((u - now_utc()).total_seconds())
            mode.update(mode='run', until=local_hm(u),
                        left=f'{s // 3600} 小时 {s % 3600 // 60} 分' if s >= 3600 else f'{s // 60} 分钟')

        ac, pct = power.os_power()
        st = load_json(BATTERY_STATE, {}) or {}
        ec, win, b = st.get('ec') or {}, st.get('win') or {}, st.get('boost') or {}
        band = list(st.get('target') or [bc.DAILY_START, bc.DAILY_STOP])
        if ac:
            if win.get('charge_mw'):
                flow = f"接通电源 · 正在充电 {win['charge_mw'] / 1000:.1f} W"
            elif isinstance(pct, int) and pct >= band[1] - 1:
                flow = f'接通电源 · 已到 {band[1]}%，不再充'
            else:
                flow = '接通电源 · 没在充电'
        else:
            flow = '使用电池' + (f" · 放电 {win['discharge_mw'] / 1000:.1f} W" if win.get('discharge_mw') else '')
        battery = {
            'pct': pct, 'ac': ac, 'flow': flow, 'band': band,
            'hw': [ec.get('hw_start'), ec.get('hw_stop')] if ec.get('hw_ok') else None,
            'temp': ec.get('battery_c'), 'system_w': ec.get('system_w'), 'charge_ma': ec.get('charge_ma'),
            'hottest': ec.get('hottest_c'),
            'read_at': parse_iso(st['updated_at_utc']).astimezone().strftime('%m-%d %H:%M') if st.get('updated_at_utc') else None,
            'slow': bool(ac and win.get('charging') and ec.get('charge_ma') is not None
                         and ec['charge_ma'] < bc.LOW_CHARGE_MA and isinstance(pct, int) and pct < band[1] - 1),
            'last': '' if b.get('active') else reason_zh(b.get('last_reason')),
        }
        boost = {'active': False}
        if b.get('active'):
            held = int((now_utc() - parse_iso(b['full_since_utc'])).total_seconds() // 60) if b.get('full_since_utc') else None
            boost = {'active': True, 'target': b.get('target_stop'), 'hold': int(b.get('full_hold_minutes', 60)),
                     'held': held, 'deadline': local_hm(parse_iso(b['deadline_utc']))}
        up = st.get('ui_prefs') or {}
        return {
            'agent': bool(agent_hwnd()),
            'mode': mode,
            'power_mode': {'ac': MODE_ZH.get(power.get_power_mode(True), '?'),
                           'dc': MODE_ZH.get(power.get_power_mode(False), '?')},
            'battery': battery,
            'boost': boost,
            'boost_prefs': {'target': up.get('target_stop', 95), 'hold': up.get('full_hold_minutes', 60),
                            'maxh': up.get('max_total_hours', 4.0)},
        }

    def set_mode(self, mode, hours=None):
        if not agent_hwnd():
            return {'ok': False, 'msg': '常驻程序没在运行：先点右上角“启动”。'}
        hours = int(hours) if hours in RUN_HOURS else 12
        if mode == 'run':
            save_json(PREFS_FILE, {**(load_json(PREFS_FILE, {}) or {}), 'hours': hours})
        save_json(REQUEST_FILE, {'mode': mode, 'hours': hours, 'requested_at_utc': iso(now_utc())})
        notify_agent()
        for _ in range(20):            # the agent consumes the request within a few hundred ms
            time.sleep(0.1)
            if not REQUEST_FILE.exists():
                break
        return {'ok': True}

    def boost(self, target, hold, maxh):
        ok = bc.request_boost(int(target), int(hold), float(maxh))
        notify_agent()
        if not ok:
            return {'ok': False, 'msg': '计划任务 Battery-ChargeLimit 没有响应：以管理员身份运行一次 install.ps1，'
                                       '或看 battery-charge\\var\\error.log。'}
        return {'ok': True}

    def start_agent(self):
        subprocess.run(['schtasks', '/Run', '/TN', AGENT_TASK], capture_output=True, creationflags=NO_WINDOW)
        for _ in range(30):
            time.sleep(0.1)
            if agent_hwnd():
                return {'ok': True}
        return {'ok': False, 'msg': '没能启动常驻程序：以管理员身份运行一次 install.ps1。'}


def set_window_icon(title):
    """The WinForms window shows python's icon: give it ours (top-level windows of this process titled `title`)."""
    ico = str(HERE / 'icon.ico')
    u32.LoadImageW.restype = wt.HANDLE
    big = u32.LoadImageW(None, ico, 1, 48, 48, 0x10)
    small = u32.LoadImageW(None, ico, 1, 16, 16, 0x10)
    pid = os.getpid()
    WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def cb(hwnd, _):
        p = wt.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        buf = ctypes.create_unicode_buffer(64)
        u32.GetWindowTextW(hwnd, buf, 64)
        if p.value == pid and buf.value == title:
            u32.SendMessageW(hwnd, 0x80, 1, big)     # WM_SETICON, ICON_BIG
            u32.SendMessageW(hwnd, 0x80, 0, small)   # ICON_SMALL
        return True
    u32.EnumWindows(WNDENUMPROC(cb), 0)


def main():
    import webview
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('PowerManager')
    except Exception:
        pass
    w = webview.create_window('PowerManager', url=(HERE / 'web' / 'index.html').as_uri(), js_api=Api(),
                              width=720, height=880, min_size=(520, 600), background_color='#F3F3F3')
    w.events.shown += lambda: set_window_icon('PowerManager')
    webview.start(gui='edgechromium', debug='--debug' in sys.argv)


if __name__ == '__main__':
    main()
