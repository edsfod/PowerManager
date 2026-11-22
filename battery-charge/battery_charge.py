"""battery-charge: keep the Huawei MateBook EC charge threshold at 75%-80%, with a temporary boost.

Daily baseline (75%-80%) is hardcoded; the UI / CLI can only start a temporary boost to X%
(85-100), which reverts to the baseline when any of these holds:
  1. plugged in at >= target% for `full_hold_minutes` (default 60);
  2. unplugged after reaching the target, or on battery for 2 consecutive checks;
  3. `max_total_hours` (default 4) passed since the boost started.

EC interface (root\\wmi OemWMIMethod.OemWMIfun, admin only), see
../notes/battery-wmi-decompile-20261111.md:
  - writes: only 03 10 (SBTT, charge start/stop %, EC 0x0268);
  - reads:  03 11 (GBTT), 03 16 (charge mode), 03 13 (resume-charging flag),
            02 0D 00 (charger charge current), 02 05 00 (system power),
            02 02 n (temperature sensors), 02 0C 15 (battery NTC).
  03 12 is PC Manager's "resume charging" button and 03 15 its charge-mode setting; neither
  is written. The EC lowers the charge current by itself when the machine is hot; this tool
  never touches that.

See README.md in the same directory.
"""
import argparse
import ctypes
import datetime as dt
import json
import subprocess
import sys
import time
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
VAR = HERE / 'var'
STATE_FILE = VAR / 'state.json'
REQ_FILE = VAR / 'request.json'
ERR_FILE = VAR / 'error.log'
EC_LOG = VAR / 'ec.log'
HISTORY = VAR / 'history.log'
# Windows PowerShell 5.1, not pwsh: the task also runs before anyone logs on (S4U, at boot),
# and the Store pwsh is only an App Execution Alias inside a logged-on user's session.
POWERSHELL = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
HISTORY_MAX = 1_000_000
TASK_NAME = 'Battery-ChargeLimit'

# Baseline protection threshold (hardcoded by design; only editable in source code)
DAILY_START = 75
DAILY_STOP = 80

# Below this charge current (mA) while charging under the threshold, the EC is throttling.
LOW_CHARGE_MA = 1000

NO_WINDOW = 0x08000000


class SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [
        ('ACLineStatus', ctypes.c_ubyte),
        ('BatteryFlag', ctypes.c_ubyte),
        ('BatteryLifePercent', ctypes.c_ubyte),
        ('SystemStatusFlag', ctypes.c_ubyte),
        ('BatteryLifeTime', ctypes.c_ulong),
        ('BatteryFullLifeTime', ctypes.c_ulong),
    ]


def get_os_power():
    """Return (ac_online: bool | None, percent: int | None) using Win32 GetSystemPowerStatus."""
    sps = SYSTEM_POWER_STATUS()
    if not ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(sps)):
        return None, None
    ac = True if sps.ACLineStatus == 1 else (False if sps.ACLineStatus == 0 else None)
    pct = int(sps.BatteryLifePercent) if sps.BatteryLifePercent <= 100 else None
    return ac, pct


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def now_utc():
    return dt.datetime.now(dt.timezone.utc)


def iso(t):
    return t.strftime('%Y-%m-%dT%H:%M:%SZ')


def parse_iso(s):
    return dt.datetime.fromisoformat(s.replace('Z', '+00:00'))


# ---------------------------------------------------------------- EC (admin)

_EC_PS = r"""
$ErrorActionPreference = 'Stop'
$i = Get-CimInstance -Namespace root\wmi -ClassName OemWMIMethod
function C([byte[]]$h) {
    [byte[]]$in = $h + @(0) * (64 - $h.Length)
    $r = Invoke-CimMethod -InputObject $i -MethodName OemWMIfun -Arguments @{u8Input = $in}
    , [int[]]($r.u8Output | Select-Object -First 8)
}
$set = $null
if (__SET__) {
    $set = C @(0x03, 0x10, __START__, __STOP__)
}
$temps = @{}
for ($n = 0; $n -le 15; $n++) {
    $o = C @(0x02, 0x02, $n)
    if ($o[0] -eq 0) { $v = $o[2] + $o[3] / 10.0; if ($o[1] -eq 1) { $v = -$v }; $temps["$n"] = $v }
}
$b = Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select-Object -First 1
$f = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity | Select-Object -First 1
@{
    set   = $set
    gbtt  = C @(0x03, 0x11)
    gbcm  = C @(0x03, 0x16)
    gbac  = C @(0x03, 0x13)
    chg   = C @(0x02, 0x0D, 0x00)
    sysp  = C @(0x02, 0x05, 0x00)
    batt  = C @(0x02, 0x0C, 0x15)
    temps = $temps
    win   = @{ online = $b.PowerOnline; charging = $b.Charging; charge_mw = $b.ChargeRate;
               discharge_mw = $b.DischargeRate; remaining_mwh = $b.RemainingCapacity;
               full_mwh = $f.FullChargedCapacity }
} | ConvertTo-Json -Compress -Depth 4
"""


def _w(o):
    return int(o[1]) + 256 * int(o[2])


def ec_call(set_pair=None):
    """Optionally write the EC charge threshold (03 10), then read it back with telemetry."""
    ps = _EC_PS.replace('__SET__', '$true' if set_pair else '$false')
    ps = ps.replace('__START__', str(int(set_pair[0])) if set_pair else '0')
    ps = ps.replace('__STOP__', str(int(set_pair[1])) if set_pair else '0')
    res = subprocess.run(
        [POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', ps],
        capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=60,
        creationflags=NO_WINDOW,
    )
    if res.returncode != 0:
        raise RuntimeError(f'EC WMI failed: {res.stderr.strip()[:500]}')
    raw = json.loads(res.stdout.strip().splitlines()[-1])
    gbtt, gbcm, batt = raw['gbtt'], raw['gbcm'], raw['batt']
    temps = {k: round(float(v), 1) for k, v in (raw.get('temps') or {}).items()}
    ec = {
        'hw_ok': gbtt[0] == 0 and gbtt[1] != 0xFF and gbtt[2] != 0xFF,
        'hw_start': gbtt[1],
        'hw_stop': gbtt[2],
        'charge_mode_raw': gbcm[:5],
        'resume_flag_raw': raw['gbac'][1],
        'charge_ma': _w(raw['chg']) if raw['chg'][0] == 0 else None,
        'system_w': raw['sysp'][1] if raw['sysp'][0] == 0 else None,
        'battery_c': round(_w(batt) / 10 - 273.15, 1) if batt[0] == 0 and _w(batt) else None,
        'sensors_c': temps,
        'hottest_c': max(temps.values()) if temps else None,
    }
    if raw.get('set') is not None:
        ec['set_status'] = raw['set'][0]
    win = raw.get('win') or {}
    return ec, win


# ---------------------------------------------------------------- files

def load_json(path, default=None):
    if not path.exists():
        return default
    try:
        return json.loads(path.read_text(encoding='utf-8'))
    except Exception:
        return default


def save_json(path, obj):
    VAR.mkdir(exist_ok=True)
    tmp = path.with_suffix('.tmp')
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    tmp.replace(path)


def append_line(path, line, max_bytes=None):
    VAR.mkdir(exist_ok=True)
    if max_bytes and path.exists() and path.stat().st_size > max_bytes:
        path.replace(path.with_name(path.name + '.1'))
    with open(path, 'a', encoding='utf-8', newline='\n') as f:
        f.write(line + '\n')


# ---------------------------------------------------------------- policy (pure)

def new_boost(req, now, ac_online, pct):
    target = max(85, min(100, int(req.get('target_stop', 95))))
    hold_min = max(5, min(720, int(req.get('full_hold_minutes', 60))))
    max_hrs = max(0.5, min(24.0, float(req.get('max_total_hours', 4.0))))
    return {
        'active': True,
        'target_start': target - 5,
        'target_stop': target,
        'full_hold_minutes': hold_min,
        'max_total_hours': max_hrs,
        'started_at_utc': iso(now),
        'deadline_utc': iso(now + dt.timedelta(hours=max_hrs)),
        'full_since_utc': iso(now) if (ac_online and pct >= target) else None,
        'reached_target': pct >= target,
        'unplugged_checks': 0,
        'last_reason': f'Boost started -> {target - 5}%-{target}% (hold {hold_min}m, max {max_hrs}h)',
    }


def step_boost(boost, now, ac_online, pct):
    """Advance an active boost by one check. Returns the revert reason, or None to keep it."""
    target = int(boost['target_stop'])
    hold_min = int(boost['full_hold_minutes'])
    if now >= parse_iso(boost['deadline_utc']):
        return f'Max boost duration ({boost["max_total_hours"]}h) elapsed'
    if pct >= target:
        boost['reached_target'] = True
    if ac_online:
        boost['unplugged_checks'] = 0
        if pct >= target:
            if not boost.get('full_since_utc'):
                boost['full_since_utc'] = iso(now)
            elif (now - parse_iso(boost['full_since_utc'])).total_seconds() >= hold_min * 60:
                return f'Held >= {target}% on AC for {hold_min} minutes'
        else:
            boost['full_since_utc'] = None
    else:
        boost['full_since_utc'] = None
        boost['unplugged_checks'] = int(boost.get('unplugged_checks', 0)) + 1
        if boost.get('reached_target') or boost['unplugged_checks'] >= 2:
            return 'Unplugged on battery'
    return None


def decide(boost, req, now, ac_online, pct):
    """One policy step. Returns (boost, (start, stop), event or None)."""
    event = None
    if req:
        boost = new_boost(req, now, ac_online, pct)
        event = boost['last_reason']
    elif boost and boost.get('active'):
        reason = step_boost(boost, now, ac_online, pct)
        if reason:
            boost['active'] = False
            boost['reverted_at_utc'] = iso(now)
            boost['last_reason'] = f'Auto-reverted to {DAILY_START}%-{DAILY_STOP}%: {reason}'
            event = boost['last_reason']
    if boost and boost.get('active'):
        return boost, (int(boost['target_start']), int(boost['target_stop'])), event
    return boost, (DAILY_START, DAILY_STOP), event


# ---------------------------------------------------------------- task

def run_task_daemon():
    """Run inside the elevated scheduled task `Battery-ChargeLimit`."""
    VAR.mkdir(exist_ok=True)
    state = load_json(STATE_FILE, {}) or {}
    now = now_utc()
    ac_online, pct = get_os_power()
    prev = state.get('os_power') or {}
    if ac_online is None:
        ac_online = bool(prev.get('ac_online', False))
    if pct is None:
        pct = int(prev.get('percent', 0))

    req = load_json(REQ_FILE)
    if req is not None:
        REQ_FILE.unlink(missing_ok=True)

    boost, target, event = decide(state.get('boost'), req, now, ac_online, pct)
    if event:
        append_line(EC_LOG, f'{iso(now)} {event}')

    ec, win = ec_call()
    if not ec['hw_ok'] or (ec['hw_start'], ec['hw_stop']) != target:
        was = f"{ec['hw_start']}-{ec['hw_stop']}" if ec['hw_ok'] else 'unreadable'
        ec, win = ec_call(target)
        ok = ec['hw_ok'] and (ec['hw_start'], ec['hw_stop']) == target
        append_line(EC_LOG, f'{iso(now)} set {target[0]}-{target[1]} (was {was}, battery {pct}%, '
                            f'{"AC" if ac_online else "DC"}) -> readback {ec["hw_start"]}-{ec["hw_stop"]}'
                            f'{"" if ok else " MISMATCH"}')
        if not ok:
            raise RuntimeError(f'EC threshold readback mismatch: want {target}, got {ec}')

    if boost is None:
        boost = {'active': False, 'last_reason': f'Daily baseline {DAILY_START}%-{DAILY_STOP}%'}
    state.update({
        'updated_at_utc': iso(now),
        'target': list(target),
        'ec': ec,
        'win': win,
        'os_power': {'ac_online': ac_online, 'percent': pct},
        'boost': boost,
    })
    if req:
        state['ui_prefs'] = {
            'target_stop': boost['target_stop'],
            'full_hold_minutes': boost['full_hold_minutes'],
            'max_total_hours': boost['max_total_hours'],
        }
    save_json(STATE_FILE, state)
    append_line(HISTORY, ' '.join([
        iso(now), f'pct={pct}', 'AC' if ac_online else 'DC',
        f"hw={ec['hw_start']}-{ec['hw_stop']}", f"chg_mW={win.get('charge_mw')}",
        f"dis_mW={win.get('discharge_mw')}", f"cap_mWh={win.get('remaining_mwh')}",
        f"chg_mA={ec['charge_ma']}", f"sys_W={ec['system_w']}", f"hot_C={ec['hottest_c']}",
        f"batt_C={ec['battery_c']}", f"boost={'on' if boost.get('active') else 'off'}",
    ]), max_bytes=HISTORY_MAX)


def trigger_scheduled_task(wait_sec=4.0):
    """Trigger `Battery-ChargeLimit` task from an unprivileged process and wait for state update."""
    old_mtime = STATE_FILE.stat().st_mtime if STATE_FILE.exists() else 0
    if is_admin():
        run_task_daemon()
        return True
    res = subprocess.run(
        ['schtasks', '/Run', '/TN', TASK_NAME],
        capture_output=True, text=True, creationflags=NO_WINDOW
    )
    if res.returncode != 0:
        return False
    t0 = time.monotonic()
    while time.monotonic() - t0 < wait_sec:
        time.sleep(0.25)
        if not STATE_FILE.exists():
            continue
        mtime = STATE_FILE.stat().st_mtime
        if mtime > old_mtime:
            if not REQ_FILE.exists():
                return True
            # A periodic task was already in flight and finished without
            # consuming REQ_FILE (IgnoreNew); re-trigger now that it exited.
            old_mtime = mtime
            subprocess.run(
                ['schtasks', '/Run', '/TN', TASK_NAME],
                capture_output=True, text=True, creationflags=NO_WINDOW
            )
    return False


# ---------------------------------------------------------------- display

def format_status_text(state):
    state = state or {}
    ac_online, pct = get_os_power()
    prev = state.get('os_power') or {}
    if ac_online is None:
        ac_online = bool(prev.get('ac_online', False))
    if pct is None:
        pct = prev.get('percent', '?')
    ec = state.get('ec') or {}
    win = state.get('win') or {}
    boost = state.get('boost') or {}
    target = state.get('target') or [DAILY_START, DAILY_STOP]

    if win.get('charge_mw'):
        flow = f"充电 {win['charge_mw'] / 1000:.1f} W"
    elif win.get('discharge_mw'):
        flow = f"放电 {win['discharge_mw'] / 1000:.1f} W"
    else:
        flow = '不充不放'
    lines = [f"当前电量：{pct}%（{'接通电源' if ac_online else '使用电池'}，{flow}）"]

    if ec.get('hw_ok'):
        hw = f"{ec['hw_start']}%–{ec['hw_stop']}%"
        note = '' if [ec['hw_start'], ec['hw_stop']] == list(target) else f"（应为 {target[0]}%–{target[1]}%，下次检查会改回）"
        lines.append(f"硬件阈值：{hw}{note}（日常保护基准 {DAILY_START}%–{DAILY_STOP}%）")
    else:
        lines.append('硬件阈值：还没读到（计划任务运行后才有）')

    parts = []
    if ec.get('charge_ma') is not None:
        parts.append(f"充电电流 {ec['charge_ma']} mA")
    if ec.get('hottest_c') is not None:
        parts.append(f"最热传感器 {ec['hottest_c']:.0f} °C")
    if ec.get('battery_c') is not None:
        parts.append(f"电池 {ec['battery_c']:.1f} °C")
    if ec.get('system_w') is not None:
        parts.append(f"整机 {ec['system_w']} W")
    if parts:
        lines.append('EC 读数：' + '，'.join(parts))
    if (ac_online and win.get('charging') and ec.get('charge_ma') is not None
            and ec['charge_ma'] < LOW_CHARGE_MA and isinstance(pct, int) and pct < target[1] - 1):
        lines.append('充得慢：EC 在发热或整机功耗高时会压低充电电流（保护），机器凉下来会逐档放开。')

    if boost.get('active'):
        now = now_utc()
        rem_min = max(0, int((parse_iso(boost['deadline_utc']) - now).total_seconds() // 60))
        hold_info = f"达标后插电保持 {boost.get('full_hold_minutes', 60)} 分钟回落"
        if boost.get('full_since_utc'):
            held = max(0, int((now - parse_iso(boost['full_since_utc'])).total_seconds() // 60))
            hold_info = f"已达标保持 {held}/{boost.get('full_hold_minutes', 60)} 分钟"
        lines.append(f"当前模式：临时冲高至 {boost.get('target_stop')}%（{hold_info}，兜底剩余 {rem_min} 分钟）")
    else:
        lines.append(f"当前模式：日常保护（{boost.get('last_reason', '75%–80%')}）")

    if state.get('updated_at_utc'):
        upd = parse_iso(state['updated_at_utc'])
        age = int((now_utc() - upd).total_seconds() // 60)
        local = upd.astimezone().strftime('%H:%M')
        lines.append(f"读数时间：{local}（{age} 分钟前）" + ('，计划任务可能没在跑' if age > 15 else ''))
    return '\n'.join(lines)


def run_gui():
    import tkinter as tk
    from tkinter import ttk, messagebox

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('PowerManager.BatteryCharge')
    except Exception:
        pass

    state = load_json(STATE_FILE, {}) or {}
    prefs = state.get('ui_prefs', {})

    root = tk.Tk()
    root.title("电池临时冲高（自动回落 75%–80%）")
    root.resizable(False, False)
    ico_path = HERE / 'icon.ico'
    if ico_path.exists():
        try:
            root.iconbitmap(default=str(ico_path))
        except Exception:
            pass

    pad = ttk.Frame(root, padding=18)
    pad.grid(row=0, column=0, sticky="nsew")

    status_var = tk.StringVar(value=format_status_text(state))
    status_lbl = ttk.Label(pad, textvariable=status_var, justify="left", font=("Microsoft YaHei UI", 10))
    status_lbl.grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 14))

    ttk.Separator(pad, orient="horizontal").grid(row=1, column=0, columnspan=3, sticky="ew", pady=(0, 12))

    ttk.Label(pad, text="本次充电上限 (%)：", font=("Microsoft YaHei UI", 10)).grid(row=2, column=0, sticky="w", pady=5)
    target_var = tk.StringVar(value=str(prefs.get('target_stop', 95)))
    target_spin = ttk.Spinbox(pad, from_=85, to=100, increment=5, textvariable=target_var, width=8, font=("Microsoft YaHei UI", 10))
    target_spin.grid(row=2, column=1, sticky="w", padx=8, pady=5)

    quick_frame = ttk.Frame(pad)
    quick_frame.grid(row=2, column=2, sticky="w")
    for val in (90, 95, 100):
        ttk.Button(quick_frame, text=f"{val}%", width=5, command=lambda v=val: target_var.set(str(v))).pack(side="left", padx=2)

    ttk.Label(pad, text="达标且插电保持多久回落 (分钟)：", font=("Microsoft YaHei UI", 10)).grid(row=3, column=0, sticky="w", pady=5)
    hold_var = tk.StringVar(value=str(prefs.get('full_hold_minutes', 60)))
    hold_spin = ttk.Spinbox(pad, from_=10, to=360, increment=15, textvariable=hold_var, width=8, font=("Microsoft YaHei UI", 10))
    hold_spin.grid(row=3, column=1, sticky="w", padx=8, pady=5)
    ttk.Label(pad, text="（默认 60 分钟）", foreground="#666666", font=("Microsoft YaHei UI", 9)).grid(row=3, column=2, sticky="w")

    ttk.Label(pad, text="距离点击最长兜底时间 (小时)：", font=("Microsoft YaHei UI", 10)).grid(row=4, column=0, sticky="w", pady=5)
    maxh_var = tk.StringVar(value=str(prefs.get('max_total_hours', 4.0)))
    maxh_spin = ttk.Spinbox(pad, from_=1.0, to=12.0, increment=0.5, textvariable=maxh_var, width=8, font=("Microsoft YaHei UI", 10))
    maxh_spin.grid(row=4, column=1, sticky="w", padx=8, pady=5)
    ttk.Label(pad, text="（默认 4 小时；拔电出门也会自动回落）", foreground="#666666", font=("Microsoft YaHei UI", 9)).grid(row=4, column=2, sticky="w")

    def on_apply():
        try:
            t_val = int(target_var.get())
            h_val = int(hold_var.get())
            m_val = float(maxh_var.get())
            if not (85 <= t_val <= 100):
                raise ValueError("充电上限请填 85–100 之间的整数。")
            if not (5 <= h_val <= 720):
                raise ValueError("达标保持分钟数请填 5–720 之间。")
            if not (0.5 <= m_val <= 24.0):
                raise ValueError("兜底小时数请填 0.5–24 之间。")
        except ValueError as e:
            messagebox.showerror("参数有误", str(e), parent=root)
            return

        save_json(REQ_FILE, {
            'target_stop': t_val,
            'full_hold_minutes': h_val,
            'max_total_hours': m_val,
            'requested_at_utc': iso(now_utc()),
        })
        btn_apply.config(state="disabled", text="正在写入 EC...")
        root.update_idletasks()
        ok = trigger_scheduled_task(wait_sec=8.0)
        status_var.set(format_status_text(load_json(STATE_FILE, {})))
        btn_apply.config(state="normal", text="开始临时冲高（到期自动回 80%）")
        if not ok:
            messagebox.showwarning(
                "计划任务没有响应",
                "未检测到计划任务 Battery-ChargeLimit 响应。\n请先以管理员身份运行一次 install_task.ps1，"
                "或看 var\\error.log。",
                parent=root
            )

    btn_apply = ttk.Button(pad, text="开始临时冲高（到期自动回 80%）", command=on_apply)
    btn_apply.grid(row=5, column=0, columnspan=3, sticky="ew", pady=(14, 0), ipady=4)

    root.mainloop()


def main():
    parser = argparse.ArgumentParser(description="Huawei MateBook EC charge limit manager.")
    parser.add_argument('--task', action='store_true', help="Run periodic check (requires admin / scheduled task).")
    parser.add_argument('--status', action='store_true', help="Print current status to stdout.")
    parser.add_argument('--boost', type=int, metavar='PCT', help="Start a temporary boost to PCT%% (85-100) from CLI.")
    parser.add_argument('--hold-min', type=int, default=60, help="Full-charge hold minutes before auto-revert (default: 60).")
    parser.add_argument('--max-hours', type=float, default=4.0, help="Max total hours before auto-revert (default: 4.0).")
    args = parser.parse_args()

    if args.task:
        run_task_daemon()
        return

    if args.boost is not None:
        save_json(REQ_FILE, {
            'target_stop': max(85, min(100, args.boost)),
            'full_hold_minutes': args.hold_min,
            'max_total_hours': args.max_hours,
            'requested_at_utc': iso(now_utc()),
        })
        trigger_scheduled_task(wait_sec=8.0)
        print(format_status_text(load_json(STATE_FILE, {})))
        return

    if args.status:
        print(format_status_text(load_json(STATE_FILE, {})))
        return

    run_gui()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        VAR.mkdir(exist_ok=True)
        with open(ERR_FILE, 'a', encoding='utf-8') as f:
            f.write(f'{iso(now_utc())}\n{traceback.format_exc()}\n')
        try:
            append_line(EC_LOG, f'{iso(now_utc())} ERROR {traceback.format_exc().strip().splitlines()[-1]}')
        except Exception:
            pass
        sys.exit(1)
