"""battery-charge: keep the Huawei MateBook EC charge threshold at 75%-80%, with a temporary boost.

Daily baseline (75%-80%) is hardcoded; the UI / CLI can only start a temporary boost to X%
(85-100), which reverts to the baseline when any of these holds:
  1. plugged in at >= target% for `full_hold_minutes` (default 60);
  2. unplugged after reaching the target, or unplugged for 10 minutes before reaching it;
  3. `max_total_hours` (default 4) passed since the boost started.

`--task` runs once in the elevated scheduled task `Battery-ChargeLimit` and exits. It is started by
events, not by a clock (REQUIREMENTS.md R19): boot, logon, resume, power source change (task
triggers), and by the agent (../agent/) at every 5% of battery, at the boost's due times and every
3 minutes while commuting; the agent leaves the reason in var/kick.json. Every run writes one line
to var/history.log.

EC access: ../common/ec.py (root\\wmi OemWMIMethod.OemWMIfun through comtypes, admin only); the EC
interface is documented in ../notes/battery-wmi-decompile-20261111.md. Only 03 10 (charge
start/stop %) is written. The EC lowers the charge current by itself when the machine is hot; this
tool never touches that.

See README.md in the same directory.
"""
import argparse
import ctypes
import datetime as dt
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from common.power import os_power                                                  # noqa: E402
from common.util import (BATTERY_KICK, BATTERY_STATE, BATTERY_TASK, append_line, iso, load_json,  # noqa: E402
                         now_utc, parse_iso, save_json)

HERE = Path(__file__).resolve().parent
VAR = HERE / 'var'
STATE_FILE = BATTERY_STATE
REQ_FILE = VAR / 'request.json'
ERR_FILE = VAR / 'error.log'
EC_LOG = VAR / 'ec.log'
HISTORY = VAR / 'history.log'
HISTORY_MAX = 1_000_000
TASK_NAME = BATTERY_TASK

# Baseline protection threshold (hardcoded by design; only editable in source code)
DAILY_START = 75
DAILY_STOP = 80
UNPLUG_REVERT_MIN = 10

# Below this charge current (mA) while charging under the threshold, the EC is throttling.
LOW_CHARGE_MA = 1000
NO_WINDOW = 0x08000000


def get_os_power():
    return os_power()


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


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
        'unplugged_since_utc': None if ac_online else iso(now),
        'last_reason': f'Boost started -> {target - 5}%-{target}% (hold {hold_min}m, max {max_hrs}h)',
    }


def step_boost(boost, now, ac_online, pct):
    """Advance an active boost at one run. Returns the revert reason, or None to keep it."""
    target = int(boost['target_stop'])
    hold_min = int(boost['full_hold_minutes'])
    if now >= parse_iso(boost['deadline_utc']):
        return f'Max boost duration ({boost["max_total_hours"]}h) elapsed'
    if pct >= target:
        boost['reached_target'] = True
    if ac_online:
        boost['unplugged_since_utc'] = None
        if pct >= target:
            if not boost.get('full_since_utc'):
                boost['full_since_utc'] = iso(now)
            elif (now - parse_iso(boost['full_since_utc'])).total_seconds() >= hold_min * 60:
                return f'Held >= {target}% on AC for {hold_min} minutes'
        else:
            boost['full_since_utc'] = None
    else:
        boost['full_since_utc'] = None
        if boost.get('reached_target'):
            return 'Unplugged on battery'
        if not boost.get('unplugged_since_utc'):
            boost['unplugged_since_utc'] = iso(now)
        elif (now - parse_iso(boost['unplugged_since_utc'])).total_seconds() >= UNPLUG_REVERT_MIN * 60:
            return f'Unplugged for {UNPLUG_REVERT_MIN} minutes'
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

def run_task():
    """One run inside the elevated scheduled task `Battery-ChargeLimit`."""
    from common import ec as ecmod

    VAR.mkdir(exist_ok=True)
    state = load_json(STATE_FILE, {}) or {}
    now = now_utc()
    ac_online, pct = get_os_power()
    prev = state.get('os_power') or {}
    if ac_online is None:
        ac_online = bool(prev.get('ac_online', False))
    if pct is None:
        pct = int(prev.get('percent', 0))

    kick = load_json(BATTERY_KICK)
    if kick is not None:
        BATTERY_KICK.unlink(missing_ok=True)
    fresh_kick = kick and (now - parse_iso(kick.get('at_utc', iso(now)))).total_seconds() < 120
    why = kick.get('reason', 'agent') if fresh_kick else 'event'
    req = load_json(REQ_FILE)
    if req is not None:
        REQ_FILE.unlink(missing_ok=True)
        why = 'boost request'

    boost, target, event = decide(state.get('boost'), req, now, ac_online, pct)
    if event:
        append_line(EC_LOG, f'{iso(now)} {event}')

    ec = ecmod.EC()
    info, win = ecmod.read_all(ec)
    if not info['hw_ok'] or (info['hw_start'], info['hw_stop']) != target:
        was = f"{info['hw_start']}-{info['hw_stop']}" if info['hw_ok'] else 'unreadable'
        info, win = ecmod.read_all(ec, target)
        ok = info['hw_ok'] and (info['hw_start'], info['hw_stop']) == target
        append_line(EC_LOG, f'{iso(now)} set {target[0]}-{target[1]} (was {was}, battery {pct}%, '
                            f'{"AC" if ac_online else "DC"}) -> readback {info["hw_start"]}-{info["hw_stop"]}'
                            f'{"" if ok else " MISMATCH"}')
        if not ok:
            raise RuntimeError(f'EC threshold readback mismatch: want {target}, got {info}')

    if boost is None:
        boost = {'active': False, 'last_reason': f'Daily baseline {DAILY_START}%-{DAILY_STOP}%'}
    state.update({
        'updated_at_utc': iso(now),
        'run_reason': why,
        'target': list(target),
        'ec': info,
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
        f"hw={info['hw_start']}-{info['hw_stop']}", f"chg_mW={win.get('charge_mw')}",
        f"dis_mW={win.get('discharge_mw')}", f"cap_mWh={win.get('remaining_mwh')}",
        f"chg_mA={info['charge_ma']}", f"sys_W={info['system_w']}", f"hot_C={info['hottest_c']}",
        f"batt_C={info['battery_c']}", f"boost={'on' if boost.get('active') else 'off'}",
        'why=' + why.replace(' ', '_'),
    ]), max_bytes=HISTORY_MAX)


def trigger_scheduled_task(wait_sec=8.0):
    """Start `Battery-ChargeLimit` from an unprivileged process and wait for state.json to change."""
    import subprocess
    old_mtime = STATE_FILE.stat().st_mtime if STATE_FILE.exists() else 0
    if is_admin():
        run_task()
        return True

    def start():
        return subprocess.run(['schtasks', '/Run', '/TN', TASK_NAME], capture_output=True, text=True,
                              creationflags=NO_WINDOW).returncode == 0
    if not start():
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
            # a run already in flight finished without seeing REQ_FILE (IgnoreNew): start again
            old_mtime = mtime
            start()
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
    lines = [f"电量：{pct}%（{'接通电源' if ac_online else '使用电池'}，读数时 {flow}）"]

    if ec.get('hw_ok'):
        hw = f"{ec['hw_start']}%–{ec['hw_stop']}%"
        note = '' if [ec['hw_start'], ec['hw_stop']] == list(target) else f"（应为 {target[0]}%–{target[1]}%，下次运行会改回）"
        lines.append(f"充电阈值：{hw}{note}（日常 {DAILY_START}%–{DAILY_STOP}%）")
    else:
        lines.append('充电阈值：还没读到（计划任务运行后才有）')

    parts = []
    if ec.get('charge_ma') is not None:
        parts.append(f"充电电流 {ec['charge_ma']} mA")
    if ec.get('battery_c') is not None:
        parts.append(f"电池 {ec['battery_c']:.1f} °C")
    if ec.get('hottest_c') is not None:
        parts.append(f"最热传感器 {ec['hottest_c']:.0f} °C")
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
        lines.append(f"冲高中：到 {boost.get('target_stop')}%（{hold_info}，最迟 {rem_min} 分钟后回落）")
    else:
        lines.append(f"日常保护（{boost.get('last_reason', '75%–80%')}）")

    if state.get('updated_at_utc'):
        upd = parse_iso(state['updated_at_utc'])
        lines.append(f"读数时间：{upd.astimezone().strftime('%m-%d %H:%M')}（有事才读：插拔电、醒来、电量每变 5%）")
    return '\n'.join(lines)


def request_boost(target_stop, hold_min=60, max_hours=4.0):
    save_json(REQ_FILE, {
        'target_stop': max(85, min(100, int(target_stop))),
        'full_hold_minutes': int(hold_min),
        'max_total_hours': float(max_hours),
        'requested_at_utc': iso(now_utc()),
    })
    return trigger_scheduled_task(wait_sec=8.0)


def main():
    parser = argparse.ArgumentParser(description="Huawei MateBook EC charge limit manager (UI: ../ui/ui.py).")
    parser.add_argument('--task', action='store_true', help="One run (scheduled task, admin).")
    parser.add_argument('--status', action='store_true', help="Print current status.")
    parser.add_argument('--boost', type=int, metavar='PCT', help="Start a temporary boost to PCT%% (85-100).")
    parser.add_argument('--hold-min', type=int, default=60, help="Full-charge hold minutes before auto-revert (default: 60).")
    parser.add_argument('--max-hours', type=float, default=4.0, help="Max total hours before auto-revert (default: 4.0).")
    args = parser.parse_args()

    if args.task:
        run_task()
    elif args.boost is not None:
        request_boost(args.boost, args.hold_min, args.max_hours)
        print(format_status_text(load_json(STATE_FILE, {})))
    else:
        print(format_status_text(load_json(STATE_FILE, {})))


if __name__ == '__main__':
    try:
        main()
    except Exception:
        VAR.mkdir(exist_ok=True)
        append_line(ERR_FILE, f'{iso(now_utc())}\n{traceback.format_exc()}')
        try:
            append_line(EC_LOG, f'{iso(now_utc())} ERROR {traceback.format_exc().strip().splitlines()[-1]}')
        except Exception:
            pass
        sys.exit(1)
