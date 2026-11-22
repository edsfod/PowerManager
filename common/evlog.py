"""Read the System event log (wevtapi, no admin) and summarise what happened while the screen was off.

Events used:
- Kernel-Power 506 enter Modern Standby (Reason), 507 exit (Reason, SleepEntered, SleepDurationInUs,
  DurationInUs, LidOpenState, battery capacities);
- Power-Troubleshooter 1 resume from a low power state (SleepTime, WakeTime, TargetState 5 = hibernate);
- Kernel-Power 42 entering sleep (TargetState 5 = hibernate).
"""
import ctypes
import ctypes.wintypes as wt
import datetime as dt
import xml.etree.ElementTree as ET

NS = '{http://schemas.microsoft.com/win/2004/08/events/event}'

# Kernel-Power 506/507 Reason codes seen on this machine (2026-11-22), in Chinese.
REASONS = {
    1: '电源键', 11: '程序关屏', 12: '闲置超时', 15: '合盖', 20: '睡眠、休眠或关机',
    25: '从休眠恢复', 28: '插拔电（屏幕没亮）', 32: '鼠标', 31: '键盘', 55: '待机耗电超预算',
}
OPEN_REASONS = {15: '开盖', 32: '鼠标', 31: '键盘', 1: '电源键'}   # wording when the reason ends a standby


def reason_text(code, exiting=False):
    if exiting and code in OPEN_REASONS:
        return OPEN_REASONS[code]
    return REASONS.get(code, f'原因代码 {code}')


# ------------------------------------------------------------------ reading

_wevt = None


def _api():
    global _wevt
    if _wevt is None:
        _wevt = ctypes.WinDLL('wevtapi')
        _wevt.EvtQuery.restype = wt.HANDLE
        _wevt.EvtQuery.argtypes = [wt.HANDLE, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD]
        _wevt.EvtNext.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE), wt.DWORD, wt.DWORD,
                                  ctypes.POINTER(wt.DWORD)]
        _wevt.EvtRender.argtypes = [wt.HANDLE, wt.HANDLE, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                                    ctypes.POINTER(wt.DWORD), ctypes.POINTER(wt.DWORD)]
        _wevt.EvtClose.argtypes = [wt.HANDLE]
    return _wevt


def _parse(xml):
    root = ET.fromstring(xml)
    sysn = root.find(f'{NS}System')
    prov = sysn.find(f'{NS}Provider').get('Name')
    eid = int(sysn.find(f'{NS}EventID').text)
    t = sysn.find(f'{NS}TimeCreated').get('SystemTime')
    when = dt.datetime.fromisoformat(t[:26].rstrip('Z') + '+00:00') if '.' in t else \
        dt.datetime.fromisoformat(t.replace('Z', '+00:00'))
    data = {}
    for d in root.iter(f'{NS}Data'):
        if d.get('Name'):
            data[d.get('Name')] = d.text
    return {'provider': prov, 'id': eid, 'time': when, 'data': data}


def query(since, until=None):
    """Kernel-Power 42/506/507 and Power-Troubleshooter 1 events in [since, until], oldest first."""
    until = until or dt.datetime.now(dt.timezone.utc)
    f = lambda t: t.astimezone(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.000Z')
    xp = (f"*[System[(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=506 or EventID=507))"
          f" or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)]"
          f" and System[TimeCreated[@SystemTime>='{f(since)}' and @SystemTime<='{f(until)}']]]")
    api = _api()
    h = api.EvtQuery(None, 'System', xp, 0x1)   # EvtQueryChannelPath, oldest first
    if not h:
        return []
    out = []
    try:
        arr = (wt.HANDLE * 32)()
        n = wt.DWORD()
        while api.EvtNext(h, 32, arr, 2000, 0, ctypes.byref(n)):
            for i in range(n.value):
                used, props = wt.DWORD(), wt.DWORD()
                api.EvtRender(None, arr[i], 1, 0, None, ctypes.byref(used), ctypes.byref(props))
                buf = ctypes.create_unicode_buffer(used.value // 2 + 1)
                if api.EvtRender(None, arr[i], 1, used, buf, ctypes.byref(used), ctypes.byref(props)):
                    try:
                        out.append(_parse(buf.value))
                    except Exception:
                        pass
                api.EvtClose(arr[i])
    finally:
        api.EvtClose(h)
    return out


# ------------------------------------------------------------------ summary (pure)

def _int(d, k, default=0):
    try:
        return int(d.get(k))
    except (TypeError, ValueError):
        return default


def fmt_dur(seconds):
    m = int(round(seconds / 60))
    if m < 1:
        return f'{int(seconds)} 秒'
    if m < 60:
        return f'{m} 分钟'
    return f'{m // 60} 小时 {m % 60} 分' if m % 60 else f'{m // 60} 小时'


def summarize(events, t_off, t_on, notes=(), pct=(None, None)):
    """What happened between the screen going off (t_off) and on again (t_on).

    events: from query(); notes: [(time, text)] the agent recorded (e.g. why it put the machine to
    sleep or hibernated it); pct: (battery % at t_off, at t_on).
    Returns (title, [lines]).
    """
    loc = lambda t: t.astimezone().strftime('%H:%M')
    kp = lambda e, i: e['provider'].endswith('Kernel-Power') and e['id'] == i
    hib = [e for e in events if e['provider'].endswith('Troubleshooter') and _int(e['data'], 'TargetState') == 5]
    enters = [e for e in events if kp(e, 506) and _int(e['data'], 'Reason') not in (20, 25)]
    # 507 with reason 20 ends a standby because the machine is going to hibernate / shut down: not a wake
    exits = [e for e in events if kp(e, 507) and _int(e['data'], 'Reason') != 20]
    total = (t_on - t_off).total_seconds()

    slept_s = sum(_int(e['data'], 'SleepDurationInUs') for e in exits
                  if str(e['data'].get('SleepEntered')).lower() == 'true') / 1e6
    hib_s = 0.0
    for e in hib:
        try:
            a = dt.datetime.fromisoformat(e['data']['SleepTime'][:26].rstrip('Z') + '+00:00')
            b = dt.datetime.fromisoformat(e['data']['WakeTime'][:26].rstrip('Z') + '+00:00')
            hib_s += max(0.0, (b - a).total_seconds())
        except Exception:
            pass

    if hib:
        title = f'休眠了 {fmt_dur(hib_s)}'
    elif slept_s >= 60:
        title = f'睡了 {fmt_dur(slept_s)}'
    else:
        title = f'醒着，屏幕关了 {fmt_dur(total)}'

    why_in = reason_text(_int(enters[0]['data'], 'Reason')) if enters else '关屏'
    why_out = reason_text(_int(exits[-1]['data'], 'Reason'), exiting=True) if exits else '亮屏'
    agent_why = [txt for t, txt in notes if t_off <= t <= t_on]
    if agent_why:
        why_in = agent_why[0]
    lines = [f'{loc(t_off)} {why_in} → {loc(t_on)} {why_out}']

    extra = []
    brief = exits[:-1]
    if brief:
        kinds = []
        for e in brief:
            r = reason_text(_int(e['data'], 'Reason'))
            if r not in kinds:
                kinds.append(r)
        extra.append(f'期间短暂醒 {len(brief)} 次（{"、".join(kinds)}）')
    awake_s = total - slept_s - hib_s
    if (slept_s >= 60 or hib) and awake_s >= 120:
        extra.append(f'醒着共 {fmt_dur(awake_s)}')
    for txt in agent_why[1:]:
        extra.append(txt)
    if pct[0] is not None and pct[1] is not None:
        extra.append(f'电量 {pct[0]}% → {pct[1]}%')
    if extra:
        lines.append(' · '.join(extra))
    return title, lines
