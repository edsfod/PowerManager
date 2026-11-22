"""PowerManager resident agent: runs in the user's session from logon (scheduled task PowerManager-Agent).

It waits; it never polls. A hidden window receives Windows' power notifications (lid, display,
power source, battery %) and the UI's "reload" message; one waitable timer, set to the earliest
pending wall-clock due time and never waking the machine, carries the one-shot timers. Each wake-up
is handed to brain.Brain, which decides; this file only does what Brain asks.

    pythonw agent.py                    run (what the scheduled task does)
    python agent.py --status            print mode and settings
    python agent.py --restore-original  put back the Windows settings saved before the first run,
                                        stop the agent and disable its task
Logs (UTC): var/agent.log, var/error.log. Mode: var/mode.json. See ../README.md and ../DESIGN.md.
"""
import ctypes
import ctypes.wintypes as wt
import datetime as dt
import sys
import threading
import time
import traceback
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from common import evlog, power, toast                                       # noqa: E402
from common.util import (AGENT_RELOAD_MSG, AGENT_TASK, AGENT_VAR, AGENT_WINDOW_CLASS, BATTERY_KICK,  # noqa: E402
                         BATTERY_STATE, BATTERY_TASK, append_line, iso, load_json, now_utc, parse_iso,
                         save_json)
from brain import Brain                                                       # noqa: E402

MODE_FILE = AGENT_VAR / 'mode.json'
REQUEST_FILE = AGENT_VAR / 'request.json'     # written by the UI before it posts the reload message
ORIGINAL_FILE = AGENT_VAR / 'original.json'
OLD_ORIGINAL = AGENT_VAR.parent.parent / 'power-mode' / 'var' / 'original.json'
LOG = AGENT_VAR / 'agent.log'
ERR = AGENT_VAR / 'error.log'

u32, k32 = ctypes.WinDLL('user32', use_last_error=True), ctypes.WinDLL('kernel32', use_last_error=True)
GUIDS = {
    'BA3E0F4D-B817-4094-A2D1-D56379E6A0F3': 'lid',        # 0 closed, 1 open
    '6FE69556-704A-47A0-8F24-C28D936FDA47': 'display',    # 0 off, 1 on, 2 dimmed
    '5D3E9A59-E9D5-4B00-A6BD-FF34FF516548': 'source',     # 0 AC, 1 DC, 2 UPS
    'A7AD8041-B45A-4CAE-87A3-EECBB468A9E1': 'percent',
}
WM_POWERBROADCAST, PBT_POWERSETTINGCHANGE, WM_DESTROY = 0x218, 0x8013, 0x0002
WM_WTSSESSION_CHANGE, WTS_SESSION_LOCK, WTS_SESSION_UNLOCK = 0x2B1, 0x7, 0x8
LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
u32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u32.DefWindowProcW.restype = LRESULT
u32.CreateWindowExW.restype = wt.HWND
u32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]
u32.RegisterPowerSettingNotification.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD]
u32.RegisterPowerSettingNotification.restype = wt.HANDLE
u32.MsgWaitForMultipleObjectsEx.argtypes = [wt.DWORD, ctypes.POINTER(wt.HANDLE), wt.DWORD, wt.DWORD, wt.DWORD]
k32.CreateWaitableTimerW.restype = wt.HANDLE
k32.SetWaitableTimer.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_longlong), wt.LONG, ctypes.c_void_p,
                                 ctypes.c_void_p, wt.BOOL]
k32.CancelWaitableTimer.argtypes = [wt.HANDLE]
k32.CreateMutexW.restype = wt.HANDLE


class WNDCLASSW(ctypes.Structure):
    _fields_ = [('style', wt.UINT), ('lpfnWndProc', WNDPROC), ('cbClsExtra', ctypes.c_int), ('cbWndExtra', ctypes.c_int),
                ('hInstance', wt.HINSTANCE), ('hIcon', wt.HICON), ('hCursor', wt.HANDLE), ('hbrBackground', wt.HBRUSH),
                ('lpszMenuName', wt.LPCWSTR), ('lpszClassName', wt.LPCWSTR)]


class PBS(ctypes.Structure):
    _fields_ = [('guid', power.GUID), ('len', wt.DWORD), ('data', ctypes.c_ubyte * 4)]


def log(msg):
    append_line(LOG, f'{iso(now_utc())} {msg}', max_bytes=1_000_000)


def log_error(where):
    append_line(ERR, f'{iso(now_utc())} {where}\n{traceback.format_exc()}', max_bytes=1_000_000)


def read_boost():
    """battery-charge's boost state, in the shape Brain uses (times as datetimes)."""
    b = (load_json(BATTERY_STATE, {}) or {}).get('boost') or {}
    if not b.get('active'):
        return {'active': False}
    t = lambda k: parse_iso(b[k]) if b.get(k) else None
    return {'active': True, 'target': int(b['target_stop']), 'deadline': t('deadline_utc'),
            'full_since': t('full_since_utc'), 'hold_min': int(b.get('full_hold_minutes', 60)),
            'reached': bool(b.get('reached_target')), 'unplugged_since': t('unplugged_since_utc')}


class IO:
    """What Brain may do to the machine."""

    def __init__(self, agent):
        self.agent = agent
        self.modes = {}

    def log(self, msg):
        log(msg)

    def apply_profile(self, profile):
        changed = power.write_settings(profile)
        if changed:
            log('settings ' + ' '.join(changed))

    def set_power_mode(self, ac, name):
        if self.modes.get(ac) == name:
            return
        if power.get_power_mode(ac) != name:
            power.set_power_mode(ac, name)
            log(f'power mode {"AC" if ac else "DC"} -> {name}')
        self.modes[ac] = name

    def hibernate(self):
        log('hibernate')
        if not power.hibernate():
            log(f'hibernate refused (error {ctypes.get_last_error()})')

    def idle_seconds(self):
        return power.idle_seconds()

    def external_display(self):
        return power.external_display_active()

    def kick_ec(self, reason):
        save_json(BATTERY_KICK, {'reason': reason, 'at_utc': iso(now_utc())})
        try:
            import comtypes
            import comtypes.client as cc
            comtypes.CoInitialize()
            svc = cc.CreateObject('Schedule.Service', dynamic=True)
            svc.Connect()
            svc.GetFolder('\\').GetTask(BATTERY_TASK).Run('')
        except Exception:
            log_error(f'start task {BATTERY_TASK}')

    def read_boost(self):
        return read_boost()

    def battery_temp(self):
        st = load_json(BATTERY_STATE, {}) or {}
        try:
            if (now_utc() - parse_iso(st['updated_at_utc'])).total_seconds() > 120:
                return None
            return float(st['ec']['battery_c'])
        except Exception:
            return None

    def save_mode(self, mode, until):
        save_json(MODE_FILE, {'mode': mode, 'run_until_utc': iso(until) if until else None,
                              'changed_at_utc': iso(now_utc())})

    def arm(self, name, seconds):
        self.agent.timers[name] = now_utc() + dt.timedelta(seconds=seconds)

    def arm_at(self, name, when):
        self.agent.timers[name] = when

    def cancel(self, name):
        self.agent.timers.pop(name, None)

    def toast(self, title, lines):
        threading.Thread(target=toast.show, args=(title, lines), daemon=True).start()

    def report_wake(self, t_off, t_on, notes, pct):
        def work():
            try:
                time.sleep(15)   # Kernel-Power 507 is written a moment after the lid / display notification
                ev = evlog.query(t_off - dt.timedelta(seconds=5), t_on + dt.timedelta(seconds=15))
                title, lines = evlog.summarize(ev, t_off, t_on, notes, pct)
                log(f'wake: {title} | {" | ".join(lines)}')
                self.agent.show_when_unlocked(title, lines)
            except Exception:
                log_error('wake report')
        threading.Thread(target=work, daemon=True).start()


class Agent:
    def __init__(self):
        self.timers = {}
        self.brain = Brain(IO(self))
        self.reload_msg = u32.RegisterWindowMessageW(AGENT_RELOAD_MSG)
        self.htimer = k32.CreateWaitableTimerW(None, False, None)
        self.armed = None
        self._proc = WNDPROC(self.wndproc)
        self.locked = False
        self.pending = None
        self.lock = threading.Lock()

    def show_when_unlocked(self, title, lines):
        """Toasts that arrive on the lock screen only go to the notification center: wait for unlock."""
        with self.lock:
            if self.locked:
                self.pending = (title, lines)
                return
        toast.show(title, lines, tag='wake')

    # --------------------------------------------------- dispatch
    def call(self, fn, *args):
        try:
            fn(now_utc(), *args)
        except Exception:
            log_error(getattr(fn, '__name__', str(fn)))

    def wndproc(self, hwnd, msg, wp, lp):
        if msg == WM_POWERBROADCAST and wp == PBT_POWERSETTINGCHANGE:
            s = PBS.from_address(lp)
            kind = GUIDS.get(str(s.guid).upper())
            v = s.data[0]
            if kind in ('lid', 'display', 'source'):
                log(f'{kind} = {v}')
            if kind == 'lid':
                self.call(self.brain.on_lid, v == 1)
            elif kind == 'display':
                self.call(self.brain.on_display, v != 0)
            elif kind == 'source':
                self.call(self.brain.on_source, v == 0)
            elif kind == 'percent':
                self.call(self.brain.on_percent, int(v))
            return 1
        if msg == WM_WTSSESSION_CHANGE:
            if wp == WTS_SESSION_LOCK:
                with self.lock:
                    self.locked = True
            elif wp == WTS_SESSION_UNLOCK:
                with self.lock:
                    self.locked, pending, self.pending = False, self.pending, None
                if pending:
                    threading.Thread(target=toast.show, args=(*pending,), kwargs={'tag': 'wake'}, daemon=True).start()
            return 0
        if msg == self.reload_msg:
            self.call(self.on_reload)
            return 0
        if msg == WM_DESTROY:
            u32.PostQuitMessage(0)
            return 0
        return u32.DefWindowProcW(hwnd, msg, wp, lp)

    def on_reload(self, now):
        req = load_json(REQUEST_FILE)
        if req:
            REQUEST_FILE.unlink(missing_ok=True)
            self.brain.set_mode(now, req.get('mode', 'normal'), req.get('hours'))
        self.brain.on_boost(now, read_boost())

    def run_due(self):
        now = now_utc()
        due = sorted((t, n) for n, t in self.timers.items() if t <= now + dt.timedelta(milliseconds=500))
        for t, n in due:
            if self.timers.get(n) == t:
                del self.timers[n]
                self.call(self.brain.on_timer, n, (now - t).total_seconds())

    def rearm(self):
        if not self.timers:
            if self.armed is not None:
                k32.CancelWaitableTimer(self.htimer)
                self.armed = None
            return
        t = min(self.timers.values())
        if t == self.armed:
            return
        ft = ctypes.c_longlong(int((t - dt.datetime(1601, 1, 1, tzinfo=dt.timezone.utc)).total_seconds() * 1e7))
        k32.SetWaitableTimer(self.htimer, ctypes.byref(ft), 0, None, None, False)   # absolute; never wakes the PC
        self.armed = t

    # --------------------------------------------------- main
    def run(self):
        wc = WNDCLASSW()
        wc.lpfnWndProc = self._proc
        wc.hInstance = k32.GetModuleHandleW(None)
        wc.lpszClassName = AGENT_WINDOW_CLASS
        u32.RegisterClassW(ctypes.byref(wc))
        hwnd = u32.CreateWindowExW(0, AGENT_WINDOW_CLASS, 'PowerManager', 0, 0, 0, 0, 0, None, None, wc.hInstance, None)
        u32.ChangeWindowMessageFilterEx(hwnd, self.reload_msg, 1, None)
        ctypes.WinDLL('wtsapi32').WTSRegisterSessionNotification(hwnd, 0)   # lock / unlock

        saved = load_json(MODE_FILE, {}) or {}
        until = saved.get('run_until_utc')
        ac, pct = power.os_power()
        self.call(self.brain.start, {'mode': saved.get('mode', 'normal'), 'run_until': parse_iso(until) if until else None},
                  True, True, ac if ac is not None else True, pct, read_boost())
        for g in GUIDS:   # Windows answers each registration with the current value
            u32.RegisterPowerSettingNotification(hwnd, ctypes.byref(power.GUID.of(g)), 0)

        msg = wt.MSG()
        handles = (wt.HANDLE * 1)(self.htimer)
        while True:
            self.rearm()
            r = u32.MsgWaitForMultipleObjectsEx(1, handles, 0xFFFFFFFF, 0x04FF, 0x0004)   # QS_ALLINPUT, input-available
            if r == 0:
                self.armed = None
                self.run_due()
                continue
            while u32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
                if msg.message == 0x0012:   # WM_QUIT
                    return
                u32.TranslateMessage(ctypes.byref(msg))
                u32.DispatchMessageW(ctypes.byref(msg))
            self.run_due()


def save_original():
    if ORIGINAL_FILE.exists():
        return
    old = load_json(OLD_ORIGINAL)
    if old:   # saved by the old power-mode tool before it first changed anything (2026-11-22)
        old = old.get('settings', old)
        orig = {side: {k: old[side][k] for k in ('display', 'sleep', 'lid', 'button')} for side in ('ac', 'dc')}
    else:
        orig = power.read_settings()
    save_json(ORIGINAL_FILE, {**orig, 'saved_at_utc': iso(now_utc())})


def status():
    m = load_json(MODE_FILE, {}) or {}
    print(f"mode: {m.get('mode', 'normal')}" + (f" until {m['run_until_utc']}" if m.get('run_until_utc') else ''))
    print(f'running: {bool(u32.FindWindowW(AGENT_WINDOW_CLASS, None))}')
    print(f'settings: {power.read_settings()}')
    print(f'power mode: AC {power.get_power_mode(True)}, DC {power.get_power_mode(False)}')


def restore_original():
    orig = load_json(ORIGINAL_FILE) or load_json(OLD_ORIGINAL)
    orig = (orig or {}).get('settings', orig)
    if not orig:
        print('no original settings saved')
        return 1
    hwnd = u32.FindWindowW(AGENT_WINDOW_CLASS, None)
    if hwnd:
        u32.PostMessageW(hwnd, 0x0010, 0, 0)   # WM_CLOSE
    import subprocess
    r = subprocess.run(['schtasks', '/Change', '/TN', AGENT_TASK, '/Disable'], capture_output=True, text=True)
    changed = power.write_settings({s: {k: orig[s][k] for k in power.SETTINGS} for s in ('ac', 'dc')})
    print('restored: ' + (' '.join(changed) or 'already as saved'))
    print(f'task {AGENT_TASK}: ' + ('disabled' if r.returncode == 0 else 'could not disable (run as admin): ' + r.stderr.strip()))
    return 0


def main():
    if '--status' in sys.argv:
        return status()
    if '--restore-original' in sys.argv:
        return restore_original()
    k32.CreateMutexW(None, False, 'Local\\PowerManagerAgent')
    if ctypes.get_last_error() == 183:   # ERROR_ALREADY_EXISTS
        return 0
    AGENT_VAR.mkdir(parents=True, exist_ok=True)
    save_original()
    Agent().run()
    log('exit')


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        log_error('main')
        sys.exit(1)
