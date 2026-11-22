"""power-mode: two power behaviours, switched by hand.

normal  everyday: closing the lid or pressing the power button sleeps; idle timeouts as the user set them.
run     keep experiments running:
          plugged in  -> never sleeps on its own; lid and power button do nothing; screen off after 10 min.
          on battery  -> (commuting) keeps running for `commute_min` minutes without input, then
                         Windows sleeps (modern standby; experiments pause and resume on wake).
                         The watcher hibernates instead if: battery <= LOW_PCT, battery >= HOT_BATT_C
                         while nobody is using it, or still awake FALLBACK_MIN after the sleep was due
                         (something kept it from sleeping) and no app needs the display.

The watcher (`--watch`) runs in the user's session from logon (scheduled task PowerManager-ModeWatch)
and does nothing in normal mode. Temperatures come from ../battery-charge/var/state.json (the EC is
admin-only); on battery in run mode it asks the Battery-ChargeLimit task for a fresh reading every
2 minutes.

See README.md in the same directory.
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import datetime as dt
import json
import os
import re
import subprocess
import sys
import time
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
VAR = HERE / 'var'
MODE_FILE = VAR / 'mode.json'
ORIG_FILE = VAR / 'original.json'
LOG = VAR / 'mode.log'
ERR = VAR / 'error.log'
BATTERY_STATE = HERE.parent / 'battery-charge' / 'var' / 'state.json'
BATTERY_TASK = 'Battery-ChargeLimit'
WATCH_TASK = 'PowerManager-ModeWatch'
POWERSHELL = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
NO_WINDOW = 0x08000000

DEFAULT_COMMUTE_MIN = 20
LOW_PCT = 10          # run mode, on battery: hibernate at or below this
HOT_BATT_C = 45.0     # run mode, on battery, nobody using it: hibernate at or above this
FALLBACK_MIN = 10     # hibernate if still awake this long after the sleep was due
POLL_S = 30
REFRESH_S = 120

# powercfg: name -> (subgroup alias, setting alias). Values: seconds (0 = never) or
# button actions 0 nothing, 1 sleep, 2 hibernate, 3 shut down.
SETTINGS = {
    'sleep': ('SUB_SLEEP', 'STANDBYIDLE'),
    'display': ('SUB_VIDEO', 'VIDEOIDLE'),
    'lid': ('SUB_BUTTONS', 'LIDACTION'),
    'button': ('SUB_BUTTONS', 'PBUTTONACTION'),
}
NORMAL = {  # idle timeouts are what the user picked in Settings on 2026-11-21
    'ac': {'sleep': 5 * 3600, 'display': 5 * 3600, 'lid': 1, 'button': 1},
    'dc': {'sleep': 3600, 'display': 3600, 'lid': 1, 'button': 1},
}


def run_profile(commute_min):
    return {
        'ac': {'sleep': 0, 'display': 10 * 60, 'lid': 0, 'button': 0},
        'dc': {'sleep': commute_min * 60, 'display': 5 * 60, 'lid': 0, 'button': 0},
    }


def profile(mode, commute_min):
    return run_profile(commute_min) if mode == 'run' else NORMAL


# ---------- small helpers ----------

def utc_now():
    return dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def log(msg):
    VAR.mkdir(exist_ok=True)
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(f'{utc_now()} {msg}\n')


def load_json(p, default):
    try:
        return json.loads(Path(p).read_text(encoding='utf-8'))
    except Exception:
        return default


def save_json(p, obj):
    VAR.mkdir(exist_ok=True)
    tmp = Path(str(p) + '.tmp')
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(tmp, p)


def run(cmd, timeout=30):
    return subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace',
                          timeout=timeout, creationflags=NO_WINDOW)


def read_mode():
    m = load_json(MODE_FILE, {})
    return {'mode': m.get('mode', 'normal'), 'commute_min': int(m.get('commute_min', DEFAULT_COMMUTE_MIN)),
            'since_utc': m.get('since_utc')}


# ---------- powercfg ----------

def get_setting(sub, key):
    out = run(['powercfg', '/qh', 'SCHEME_CURRENT', sub, key]).stdout
    vals = re.findall(r'Current (AC|DC) Power Setting Index: (0x[0-9a-fA-F]+)', out)
    d = {side.lower(): int(v, 16) for side, v in vals}
    if set(d) != {'ac', 'dc'}:
        raise RuntimeError(f'powercfg /qh {sub} {key}: no AC/DC values')
    return d


def current_settings():
    res = {'ac': {}, 'dc': {}}
    for name, (sub, key) in SETTINGS.items():
        d = get_setting(sub, key)
        res['ac'][name], res['dc'][name] = d['ac'], d['dc']
    return res


def apply_profile(p):
    if not ORIG_FILE.exists():
        save_json(ORIG_FILE, {'saved_at_utc': utc_now(), 'settings': current_settings()})
    for side, flag in (('ac', '/setacvalueindex'), ('dc', '/setdcvalueindex')):
        for name, val in p[side].items():
            sub, key = SETTINGS[name]
            r = run(['powercfg', flag, 'SCHEME_CURRENT', sub, key, str(val)])
            if r.returncode != 0:
                raise RuntimeError(f'powercfg {flag} {key} {val}: {r.stdout.strip()} {r.stderr.strip()}')
    run(['powercfg', '/setactive', 'SCHEME_CURRENT'])
    now = current_settings()
    if now != {s: dict(p[s]) for s in ('ac', 'dc')}:
        raise RuntimeError(f'read back differs: {now}')


def set_mode(mode, commute_min):
    apply_profile(profile(mode, commute_min))
    save_json(MODE_FILE, {'mode': mode, 'commute_min': commute_min, 'since_utc': utc_now()})
    log(f'mode -> {mode} (commute {commute_min} min)')
    if mode == 'run':
        run(['schtasks', '/Run', '/TN', WATCH_TASK])   # make sure the watcher is up


# ---------- Windows state ----------

class SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [('ACLineStatus', ctypes.c_ubyte), ('BatteryFlag', ctypes.c_ubyte),
                ('BatteryLifePercent', ctypes.c_ubyte), ('SystemStatusFlag', ctypes.c_ubyte),
                ('BatteryLifeTime', wt.DWORD), ('BatteryFullLifeTime', wt.DWORD)]


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


def power_status():
    s = SYSTEM_POWER_STATUS()
    ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(s))
    pct = None if s.BatteryLifePercent == 255 else s.BatteryLifePercent
    return s.ACLineStatus == 1, pct


def idle_seconds():
    li = LASTINPUTINFO(ctypes.sizeof(LASTINPUTINFO), 0)
    ctypes.windll.user32.GetLastInputInfo(ctypes.byref(li))
    tick = ctypes.windll.kernel32.GetTickCount() & 0xFFFFFFFF
    return ((tick - li.dwTime) & 0xFFFFFFFF) / 1000.0


def display_required():
    """True if some app holds a display request (video, presentation)."""
    st = wt.ULONG(0)
    ok = ctypes.windll.powrprof.CallNtPowerInformation(16, None, 0, ctypes.byref(st), ctypes.sizeof(st)) == 0
    return ok and bool(st.value & 0x2)   # ES_DISPLAY_REQUIRED


def battery_temp():
    """Battery temperature from the charge task's last reading, if under 5 minutes old."""
    st = load_json(BATTERY_STATE, {})
    try:
        t = dt.datetime.strptime(st['updated_at_utc'], '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=dt.timezone.utc)
        if (dt.datetime.now(dt.timezone.utc) - t).total_seconds() > 300:
            return None
        return float(st['ec']['battery_c'])
    except Exception:
        return None


def toast(title, body):
    esc = lambda s: s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
    xml = ('<toast scenario="reminder"><visual><binding template="ToastGeneric"><text>{}</text><text>{}</text>'
           '</binding></visual><actions><action content="关闭" arguments="dismiss" activationType="system"/>'
           '</actions></toast>'.format(esc(title), esc(body)))
    ps = r'''
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$x = New-Object Windows.Data.Xml.Dom.XmlDocument; $x.LoadXml($env:PM_XML)
$id = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($id).Show([Windows.UI.Notifications.ToastNotification]::new($x))
'''
    try:
        subprocess.run([POWERSHELL, '-NoProfile', '-NonInteractive', '-Command', ps], env={**os.environ, 'PM_XML': xml},
                       capture_output=True, timeout=30, creationflags=NO_WINDOW)
    except Exception:
        pass


def hibernate(reason):
    log(f'hibernate: {reason}')
    toast('电源模式：休眠', reason)
    time.sleep(5)
    run(['shutdown', '/h'])


# ---------- watcher ----------

def decide(mode, ac, pct, idle_s, batt_c, display_req, commute_min):
    """Pure policy for the watcher: returns a reason to hibernate, a warning, or (None, None)."""
    if mode != 'run' or ac:
        return None, None
    if pct is not None and pct <= LOW_PCT:
        return f'保持运行模式下电池只剩 {pct}%', None
    if batt_c is not None and batt_c >= HOT_BATT_C:
        if idle_s >= 60:
            return f'保持运行模式下电池 {batt_c:.1f} °C，没人在用', None
        return None, f'电池 {batt_c:.1f} °C，偏热'
    if idle_s >= (commute_min + FALLBACK_MIN) * 60 and not display_req:
        return f'没有输入 {int(idle_s // 60)} 分钟，应在 {commute_min} 分钟时睡眠却没睡', None
    return None, None


def watch():
    # one watcher per session
    ctypes.windll.kernel32.CreateMutexW(None, False, 'Local\\PowerManager-ModeWatch')
    if ctypes.windll.kernel32.GetLastError() == 183:
        return
    log('watcher started')
    last_wall = time.time()
    resumed_at = 0.0
    last_refresh = 0.0
    last_warn = 0.0
    while True:
        try:
            now = time.time()
            if now - last_wall > 3 * POLL_S:          # we were asleep / suspended
                resumed_at = now
            last_wall = now
            m = read_mode()
            ac, pct = power_status()
            if m['mode'] == 'run' and not ac and now - last_refresh >= REFRESH_S:
                run(['schtasks', '/Run', '/TN', BATTERY_TASK])
                last_refresh = now
            idle = min(idle_seconds(), now - resumed_at) if resumed_at else idle_seconds()
            reason, warn = decide(m['mode'], ac, pct, idle, battery_temp(), display_required(), m['commute_min'])
            if reason:
                hibernate(reason)
                resumed_at = time.time()
            elif warn and now - last_warn > 600:
                toast('电源模式：注意', warn)
                log(f'warn: {warn}')
                last_warn = now
        except Exception:
            VAR.mkdir(exist_ok=True)
            with open(ERR, 'a', encoding='utf-8') as f:
                f.write(f'--- {utc_now()}\n{traceback.format_exc()}')
        time.sleep(POLL_S)


def watcher_running():
    h = ctypes.windll.kernel32.OpenMutexW(0x00100000, False, 'Local\\PowerManager-ModeWatch')
    if h:
        ctypes.windll.kernel32.CloseHandle(h)
        return True
    return False


# ---------- status / GUI ----------

NAMES = {'normal': '普通', 'run': '保持运行（跑实验）'}
ACTIONS = {0: '什么都不做', 1: '睡眠', 2: '休眠', 3: '关机'}


def fmt_s(v):
    return '从不' if v == 0 else (f'{v // 3600} 小时' if v % 3600 == 0 else f'{v // 60} 分钟')


def status_text():
    m = read_mode()
    ac, pct = power_status()
    try:
        cur = current_settings()
        want = profile(m['mode'], m['commute_min'])
        match = '与模式一致' if cur == want else '与模式不一致（可能在系统设置里改过）：再点一次模式按钮'
        lines = [f'{side}：闲置 {fmt_s(cur[k]["sleep"])} 睡眠，{fmt_s(cur[k]["display"])} 关屏；'
                 f'合盖{ACTIONS.get(cur[k]["lid"], cur[k]["lid"])}，电源键{ACTIONS.get(cur[k]["button"], cur[k]["button"])}'
                 for side, k in (('插电', 'ac'), ('电池', 'dc'))]
    except Exception as e:
        match, lines = f'读不到电源设置：{e}', []
    return '\n'.join([
        f'当前模式：{NAMES[m["mode"]]}' + (f'，通勤 {m["commute_min"]} 分钟' if m['mode'] == 'run' else ''),
        f'电源：{"插电" if ac else "电池"}，{pct}%',
        *lines,
        f'系统设置{match}',
        f'后台守护：{"在运行" if watcher_running() else "没在运行（保持运行模式在电池上的兜底不起作用）"}',
    ])


def run_gui():
    import tkinter as tk
    from tkinter import messagebox
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('PowerManager.PowerMode')
    except Exception:
        pass
    root = tk.Tk()
    root.title('电源模式')
    root.resizable(False, False)
    m = read_mode()
    commute = tk.IntVar(value=m['commute_min'])
    status = tk.StringVar()

    def refresh():
        status.set(status_text())

    def switch(mode):
        try:
            set_mode(mode, max(5, min(120, int(commute.get()))))
        except Exception as e:
            messagebox.showerror('电源模式', str(e))
        refresh()

    frm = tk.Frame(root, padx=16, pady=12)
    frm.pack()
    tk.Button(frm, text='普通', width=22, height=2, command=lambda: switch('normal')).grid(row=0, column=0, padx=4)
    tk.Button(frm, text='保持运行（跑实验）', width=22, height=2, command=lambda: switch('run')).grid(row=0, column=1, padx=4)
    row = tk.Frame(frm)
    row.grid(row=1, column=0, columnspan=2, pady=(10, 4), sticky='w')
    tk.Label(row, text='保持运行时，拔电后没人操作多久睡眠（分钟）：').pack(side='left')
    tk.Spinbox(row, from_=5, to=120, increment=5, width=5, textvariable=commute).pack(side='left')
    tk.Label(frm, textvariable=status, justify='left', anchor='w').grid(row=2, column=0, columnspan=2, sticky='w', pady=(8, 0))
    tk.Label(frm, fg='#666', justify='left', wraplength=520, text=(
        '普通：合盖、按电源键就睡眠。\n'
        '保持运行：插电时从不自己睡、合盖不睡；拔电后没人操作满上面的分钟数才睡眠（实验暂停，开盖继续）。'
        f'电池 ≤ {LOW_PCT}%、电池 ≥ {HOT_BATT_C:.0f} °C 且没人在用、或该睡却 {FALLBACK_MIN} 分钟还没睡，就休眠。'
    )).grid(row=3, column=0, columnspan=2, sticky='w', pady=(8, 0))
    refresh()
    root.mainloop()


def main():
    ap = argparse.ArgumentParser(description='normal / run power modes')
    ap.add_argument('--mode', choices=['normal', 'run'])
    ap.add_argument('--commute-min', type=int)
    ap.add_argument('--status', action='store_true')
    ap.add_argument('--watch', action='store_true')
    ap.add_argument('--restore-original', action='store_true', help='put back the settings saved before the first switch')
    a = ap.parse_args()
    if a.watch:
        return watch()
    if a.restore_original:
        o = load_json(ORIG_FILE, None)
        if not o:
            sys.exit('no original.json')
        apply_profile(o['settings'])
        save_json(MODE_FILE, {'mode': 'normal', 'commute_min': read_mode()['commute_min'], 'since_utc': utc_now()})
        log('restored original settings')
        print(status_text())
        return
    if a.mode:
        cm = a.commute_min or read_mode()['commute_min']
        set_mode(a.mode, max(5, min(120, cm)))
        print(status_text())
        return
    if a.status:
        print(status_text())
        return
    run_gui()


if __name__ == '__main__':
    main()
